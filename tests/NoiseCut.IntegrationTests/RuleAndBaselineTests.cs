using Dapper;
using NoiseCut.Core.Rules;
using NoiseCut.Infrastructure.Postgres;

namespace NoiseCut.IntegrationTests;

public class RuleAndBaselineTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Rule_store_loads_the_seeded_reference_rules()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();

        var result = await new RuleStore(db).LoadAsync(CancellationToken.None);

        Assert.Empty(result.Problems);
        Assert.Equal(ReferenceRules.All.Select(r => r.Id).Order(), result.Rules.Select(r => r.Id).Order());
        Assert.All(result.Rules, r => Assert.Equal(1, r.Version));
        Assert.Equal(TimeSpan.FromMinutes(20), result.Rules.Single(r => r.Id == "stuck-pickup").Definition.Timeout);
    }

    [Fact]
    public async Task Invalid_rules_are_reported_and_skipped_not_half_applied()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        await using var conn = await db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await conn.ExecuteAsync(
            """
            INSERT INTO rules (id, version, definition) VALUES
              ('broken-json', 1, '{"id":"broken-json","kind":"nonsense"}'::jsonb),
              ('bad-window', 1, '{"id":"bad-window","name":"x","kind":"windowed_count","entity":"restaurantId",
                                 "source":{"eventType":"order.delayed"},"window":{"type":"sliding","size":"PT10M","slide":"PT3M"},
                                 "condition":{"op":"gte","value":5}}'::jsonb)
            """);

        var result = await new RuleStore(db).LoadAsync(CancellationToken.None);

        Assert.Equal(3, result.Rules.Count);
        Assert.Contains(result.Problems, p => p.StartsWith("broken-json", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.StartsWith("bad-window", StringComparison.Ordinal) && p.Contains("multiple of slide", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_enabled_column_wins_over_the_json_copy()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        await using var conn = await db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await conn.ExecuteAsync("UPDATE rules SET enabled = false WHERE id = 'zone-cancellation-spike'");

        var result = await new RuleStore(db).LoadAsync(CancellationToken.None);

        Assert.False(result.Rules.Single(r => r.Id == "zone-cancellation-spike").Definition.Enabled);
    }

    [Fact]
    public async Task Baselines_use_the_exact_hour_or_fall_back_a_week_at_a_time()
    {
        await using var db = await postgres.CreateEmptyDatabaseAsync();
        await new Migrator.MigrationRunner(db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).RunAsync(CancellationToken.None);
        await using var conn = await db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await conn.ExecuteAsync("INSERT INTO zones VALUES ('z-01', 'Koramangala')");
        var tuesday1300 = new DateTimeOffset(2026, 10, 13, 13, 0, 0, TimeSpan.Zero);
        await conn.ExecuteAsync(
            "INSERT INTO zone_hourly_stats VALUES ('z-01', @h, 200, 6, 'seed'), ('z-01', @h2, 100, 5, 'seed')",
            new { h = tuesday1300, h2 = tuesday1300.AddHours(1) });

        var cache = new BaselineCache(db);
        await cache.RefreshAsync(CancellationToken.None);

        Assert.Equal(0.03, cache.GetRate("z-01", tuesday1300));
        // Five weeks later the sim clock is far past the seeded week: the same hour-of-week is reused.
        Assert.Equal(0.03, cache.GetRate("z-01", tuesday1300.AddDays(35)));
        Assert.Equal(0.05, cache.GetRate("z-01", tuesday1300.AddDays(7).AddHours(1)));
        Assert.Null(cache.GetRate("z-01", tuesday1300.AddHours(2)));
        Assert.Null(cache.GetRate("z-09", tuesday1300));
        // Older than all data (sim clock pinned to earlier today): the nearest later week is used.
        Assert.Equal(0.03, cache.GetRate("z-01", tuesday1300.AddDays(-7)));
    }
}
