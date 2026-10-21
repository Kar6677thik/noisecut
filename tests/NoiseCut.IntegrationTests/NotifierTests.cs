using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Rules;
using NoiseCut.Infrastructure.Postgres;
using NoiseCut.Notifier;

namespace NoiseCut.IntegrationTests;

public class NotifierTests(PostgresFixture postgres)
{
    private static AlertCandidate Candidate(string alertKey, CandidateStatus status = CandidateStatus.Firing) => new()
    {
        CandidateId = Guid.CreateVersion7(),
        AlertKey = alertKey,
        Status = status,
        RuleId = "restaurant-delay-burst",
        RuleVersion = 1,
        Severity = Severity.High,
        EntityType = "restaurantId",
        EntityKey = "r-017",
        GroupKey = "z-02",
        EpisodeStart = new DateTimeOffset(2026, 10, 17, 19, 33, 0, TimeSpan.Zero),
        Observed = new Dictionary<string, double> { ["count"] = 6 },
        Threshold = new Threshold("gte", 5),
        TriggerProducedAt = DateTimeOffset.UtcNow.AddMilliseconds(-50),
        DetectedAt = DateTimeOffset.UtcNow,
        Engine = new EngineInfo("test", "order", 0, 1),
    };

    private static async Task<CandidateIntake> IntakeAsync(NpgsqlDataSource db, bool dedup)
    {
        var catalog = new RuleCatalog(new RuleStore(db), NullLogger<RuleCatalog>.Instance);
        await catalog.StartAsync(CancellationToken.None);
        await catalog.Ready.Task;
        return new CandidateIntake(db, catalog, Options.Create(new NotifierOptions { Dedup = dedup }), TimeProvider.System, NullLogger<CandidateIntake>.Instance);
    }

    private static string Key() => $"restaurant-delay-burst:1:r-017:{Guid.NewGuid():N}";

    [Fact]
    public async Task A_new_candidate_creates_an_alert_and_queues_one_delivery_per_channel()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        var intake = await IntakeAsync(db, dedup: false);
        var key = Key();

        var outcome = await intake.HandleAsync(Candidate(key), CancellationToken.None);

        Assert.True(outcome.FirstTime);
        Assert.Equal(2, outcome.DeliveriesQueued); // dashboard + webhook
        await using var conn = await db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal("firing", await conn.ExecuteScalarAsync<string>("SELECT status FROM alerts WHERE alert_key = @key", new { key }));
        Assert.Equal(
            ["dashboard", "webhook"],
            await conn.QueryAsync<string>("SELECT channel FROM deliveries WHERE alert_key = @key ORDER BY channel", new { key }));
    }

    [Fact]
    public async Task Without_dedup_a_replayed_candidate_is_notified_again()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        var intake = await IntakeAsync(db, dedup: false);
        var key = Key();

        await intake.HandleAsync(Candidate(key), CancellationToken.None);
        var replay = await intake.HandleAsync(Candidate(key), CancellationToken.None);

        Assert.False(replay.FirstTime);
        Assert.Equal(2, replay.DeliveriesQueued);
        await using var conn = await db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT repeat_count FROM alert_candidates WHERE alert_key = @key", new { key }));
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT candidate_count FROM alerts WHERE alert_key = @key", new { key }));
        // Same logical notification twice: exactly what the webhook sink will count as a duplicate.
        Assert.Equal(2, await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM deliveries WHERE alert_key = @key AND channel = 'webhook' GROUP BY notification_key", new { key }));
    }

    [Fact]
    public async Task With_dedup_a_replayed_candidate_is_recorded_but_not_notified()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        var intake = await IntakeAsync(db, dedup: true);
        var key = Key();

        await intake.HandleAsync(Candidate(key), CancellationToken.None);
        var replay = await intake.HandleAsync(Candidate(key), CancellationToken.None);

        Assert.False(replay.FirstTime);
        Assert.Equal(0, replay.DeliveriesQueued);
        await using var conn = await db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM deliveries WHERE alert_key = @key", new { key }));
    }

    [Fact]
    public async Task Resolution_closes_the_alert_and_a_late_firing_does_not_reopen_it()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        var intake = await IntakeAsync(db, dedup: false);
        var key = Key();

        await intake.HandleAsync(Candidate(key), CancellationToken.None);
        await intake.HandleAsync(Candidate(key, CandidateStatus.Resolved), CancellationToken.None);
        await intake.HandleAsync(Candidate(key), CancellationToken.None);

        await using var conn = await db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        var (status, resolvedAt) = await conn.QuerySingleAsync<(string, DateTime?)>(
            "SELECT status, resolved_at FROM alerts WHERE alert_key = @key", new { key });
        Assert.Equal("resolved", status);
        Assert.NotNull(resolvedAt);
    }

    [Fact]
    public async Task Two_dispatchers_never_send_the_same_delivery_twice()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        await using (var conn = await db.OpenConnectionAsync(TestContext.Current.CancellationToken))
        {
            for (var i = 0; i < 60; i++)
            {
                await conn.ExecuteAsync(
                    "INSERT INTO deliveries (id, notification_key, alert_key, kind, channel, payload) VALUES (@id, @nk, 'k', 'firing', 'counting', '{}'::jsonb)",
                    new { id = Guid.CreateVersion7(), nk = $"n-{i}" });
            }
        }

        var channel = new CountingChannel();
        var options = Options.Create(new NotifierOptions { Dispatcher = new DispatcherOptions { BatchSize = 7 } });
        var a = new DeliveryDispatcher(db, [channel], options, NullLogger<DeliveryDispatcher>.Instance);
        var b = new DeliveryDispatcher(db, [channel], options, NullLogger<DeliveryDispatcher>.Instance);

        // Both dispatchers drain the same outbox at the same time.
        async Task DrainAsync(DeliveryDispatcher d)
        {
            while (await d.DispatchBatchAsync(CancellationToken.None) > 0)
            {
            }
        }

        await Task.WhenAll(DrainAsync(a), DrainAsync(b), DrainAsync(a));

        Assert.Equal(60, channel.Sent.Count);
        Assert.All(channel.Sent.Values, count => Assert.Equal(1, count));
        await using var check = await db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        Assert.Equal(60, await check.ExecuteScalarAsync<long>("SELECT count(*) FROM deliveries WHERE status = 'sent'"));
    }

    [Fact]
    public async Task Failed_deliveries_back_off_and_eventually_die()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        var id = Guid.CreateVersion7();
        await using var conn = await db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await conn.ExecuteAsync(
            "INSERT INTO deliveries (id, notification_key, alert_key, kind, channel, payload) VALUES (@id, 'n', 'k', 'firing', 'broken', '{}'::jsonb)",
            new { id });

        var options = Options.Create(new NotifierOptions { Dispatcher = new DispatcherOptions { MaxAttempts = 3 } });
        var dispatcher = new DeliveryDispatcher(db, [new BrokenChannel()], options, NullLogger<DeliveryDispatcher>.Instance);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Assert.Equal(1, await dispatcher.DispatchBatchAsync(CancellationToken.None));
            var (status, attempts, next) = await conn.QuerySingleAsync<(string, int, DateTime)>(
                "SELECT status, attempts, next_attempt_at FROM deliveries WHERE id = @id", new { id });
            Assert.Equal(attempt, attempts);
            Assert.Equal(attempt < 3 ? "pending" : "dead", status);
            Assert.True(next > DateTime.UtcNow.AddMilliseconds(-100));

            // Pretend the backoff has elapsed.
            await conn.ExecuteAsync("UPDATE deliveries SET next_attempt_at = now() WHERE id = @id", new { id });
        }

        Assert.Equal(0, await dispatcher.DispatchBatchAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(1, 0.5, 1)]
    [InlineData(2, 1, 2)]
    [InlineData(4, 4, 8)]
    [InlineData(20, 150, 300)]
    public void Backoff_doubles_with_jitter_and_is_capped(int attempts, double minSeconds, double maxSeconds)
    {
        for (var i = 0; i < 50; i++)
        {
            var delay = DeliveryDispatcher.Backoff(attempts, TimeSpan.FromMinutes(5)).TotalSeconds;
            Assert.InRange(delay, minSeconds, maxSeconds);
        }
    }

    private sealed class CountingChannel : IChannel
    {
        public ConcurrentDictionary<string, int> Sent { get; } = new();

        public string Name => "counting";

        public async Task SendAsync(Notifier.DeliveryRow delivery, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
        {
            await Task.Delay(5, ct); // give the other dispatcher a chance to race
            Sent.AddOrUpdate(delivery.NotificationKey, 1, (_, n) => n + 1);
        }
    }

    private sealed class BrokenChannel : IChannel
    {
        public string Name => "broken";

        public Task SendAsync(Notifier.DeliveryRow delivery, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct) =>
            throw new HttpRequestException("receiver is down");
    }
}
