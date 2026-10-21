using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Rules;
using NoiseCut.Infrastructure.Postgres;
using NoiseCut.Notifier;

namespace NoiseCut.IntegrationTests;

/// <summary>M3: cooldown, grouping, "resolved only if notified" and escalation (SPEC §7.2), against real PostgreSQL.</summary>
public class NoisePolicyTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NotifierOptions Robust => Policy(grouping: true);

    private static NotifierOptions Policy(bool grouping) => new() { Dedup = true, Cooldown = true, Grouping = grouping, Escalation = true };

    private static AlertCandidate Candidate(string ruleId, string entity, string group, int episodeMinute, CandidateStatus status = CandidateStatus.Firing) => new()
    {
        CandidateId = Guid.CreateVersion7(),
        AlertKey = $"{ruleId}:1:{entity}:2026-10-17T12:{episodeMinute:00}:00Z",
        Status = status,
        RuleId = ruleId,
        RuleVersion = 1,
        Severity = Severity.High,
        EntityType = "restaurantId",
        EntityKey = entity,
        GroupKey = group,
        EpisodeStart = new DateTimeOffset(2026, 10, 17, 12, episodeMinute, 0, TimeSpan.Zero),
        Observed = new Dictionary<string, double> { ["count"] = 6 },
        Threshold = new Threshold("gte", 5),
        TriggerProducedAt = Now,
        DetectedAt = Now,
        Engine = new EngineInfo("test", "order", 0, 1),
    };

    private static async Task<(CandidateIntake Intake, RuleCatalog Catalog, FakeTimeProvider Clock)> SetupAsync(NpgsqlDataSource db, NotifierOptions policy)
    {
        var catalog = new RuleCatalog(new RuleStore(db), NullLogger<RuleCatalog>.Instance);
        await catalog.StartAsync(CancellationToken.None);
        await catalog.Ready.Task;
        var clock = new FakeTimeProvider(Now);
        return (new CandidateIntake(db, catalog, Options.Create(policy), clock, NullLogger<CandidateIntake>.Instance), catalog, clock);
    }

    private static async Task<long> Count(NpgsqlDataSource db, string sql, object? args = null)
    {
        await using var conn = await db.OpenConnectionAsync(Ct);
        return await conn.ExecuteScalarAsync<long>(sql, args);
    }

    [Fact]
    public async Task Cooldown_suppresses_a_new_episode_for_the_same_entity_and_counts_it_on_the_earlier_alert()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        var (intake, _, clock) = await SetupAsync(db, Policy(grouping: false));

        var first = await intake.HandleAsync(Candidate("restaurant-delay-burst", "r-017", "z-02", 0), Ct);
        clock.Advance(TimeSpan.FromMinutes(5)); // inside the 15-minute cooldown
        var second = await intake.HandleAsync(Candidate("restaurant-delay-burst", "r-017", "z-02", 20), Ct);
        clock.Advance(TimeSpan.FromMinutes(15)); // cooldown over
        var third = await intake.HandleAsync(Candidate("restaurant-delay-burst", "r-017", "z-02", 40), Ct);

        Assert.Equal(("notified", "cooldown", "notified"), (first.Decision, second.Decision, third.Decision));
        Assert.Equal(1, await Count(db, "SELECT suppressed_count FROM alerts WHERE alert_key LIKE '%r-017:2026-10-17T12:00:00Z'"));
    }

    [Fact]
    public async Task A_resolution_is_never_cooled_down_but_is_skipped_if_nobody_heard_of_the_firing()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        var (intake, _, clock) = await SetupAsync(db, Policy(grouping: false));

        await intake.HandleAsync(Candidate("restaurant-delay-burst", "r-017", "z-02", 0), Ct);
        clock.Advance(TimeSpan.FromMinutes(1));
        await intake.HandleAsync(Candidate("restaurant-delay-burst", "r-017", "z-02", 20), Ct); // cooled down

        var resolvedNotified = await intake.HandleAsync(Candidate("restaurant-delay-burst", "r-017", "z-02", 0, CandidateStatus.Resolved), Ct);
        var resolvedSilent = await intake.HandleAsync(Candidate("restaurant-delay-burst", "r-017", "z-02", 20, CandidateStatus.Resolved), Ct);

        Assert.Equal("notified", resolvedNotified.Decision);
        Assert.Equal("unnotified", resolvedSilent.Decision);
    }

    [Fact]
    public async Task Alerts_in_one_zone_within_group_wait_become_one_notification()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        var (intake, catalog, _) = await SetupAsync(db, Robust);

        var decisions = new List<string>();
        foreach (var r in new[] { "r-001", "r-002", "r-003", "r-004", "r-005", "r-006" })
        {
            decisions.Add((await intake.HandleAsync(Candidate("restaurant-delay-burst", r, "z-01", 0), Ct)).Decision);
        }

        Assert.Equal(["notified", "grouped", "grouped", "grouped", "grouped", "grouped"], decisions);
        Assert.Equal(0, await Count(db, "SELECT count(*) FROM deliveries"));

        // groupWait (30 s) elapses: one message per channel, listing all six.
        await using (var conn = await db.OpenConnectionAsync(Ct))
        {
            await conn.ExecuteAsync("UPDATE notification_groups SET flush_at = now() - interval '1 second'");
        }

        var flusher = new GroupFlusher(db, catalog, Options.Create(Robust), NullLogger<GroupFlusher>.Instance);
        Assert.Equal(1, await flusher.FlushDueAsync(Ct));

        Assert.Equal(2, await Count(db, "SELECT count(*) FROM deliveries")); // dashboard + webhook
        await using var check = await db.OpenConnectionAsync(Ct);
        var payload = await check.ExecuteScalarAsync<string>("SELECT payload::text FROM deliveries WHERE channel = 'webhook'");
        using var doc = JsonDocument.Parse(payload!);
        Assert.Equal(6, doc.RootElement.GetProperty("memberCount").GetInt32());
        Assert.Contains("6 alerts in Koramangala", doc.RootElement.GetProperty("title").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, await flusher.FlushDueAsync(Ct)); // flushed exactly once
    }

    [Fact]
    public async Task An_unacknowledged_alert_escalates_once_per_step()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        var (intake, catalog, _) = await SetupAsync(db, Policy(grouping: false));
        await intake.HandleAsync(Candidate("restaurant-delay-burst", "r-017", "z-02", 0), Ct);
        await intake.HandleAsync(Candidate("restaurant-delay-burst", "r-018", "z-02", 0), Ct);

        await using (var conn = await db.OpenConnectionAsync(Ct))
        {
            // 11 minutes later (step 1 is after PT10M); r-018 was acknowledged in the meantime.
            await conn.ExecuteAsync("UPDATE alerts SET first_detected_at = now() - interval '11 minutes'");
            await conn.ExecuteAsync("UPDATE alerts SET status = 'acknowledged', acked_at = now() WHERE entity_key = 'r-018'");
        }

        var escalator = new Escalator(db, catalog, Options.Create(Robust), NullLogger<Escalator>.Instance);
        Assert.Equal(1, await escalator.EscalateDueAsync(Ct));
        Assert.Equal(0, await escalator.EscalateDueAsync(Ct));

        Assert.Equal(1, await Count(db, "SELECT count(*) FROM deliveries WHERE kind = 'escalation' AND channel = 'email' AND alert_key LIKE '%r-017%'"));
    }
}
