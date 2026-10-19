using Dapper;
using Npgsql;

namespace NoiseCut.IntegrationTests;

/// <summary>The grading views from migration 0002. Every experiment number depends on them, so they get their own tests.</summary>
public class TruthLogTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset T = new(2026, 10, 17, 6, 30, 0, TimeSpan.Zero);

    private static Task Scenario(NpgsqlConnection c, string name, string target, string[] restaurants, string? rule) =>
        c.ExecuteAsync(
            """
            INSERT INTO sim_scenarios (id, name, target, restaurants, starts_at, ends_at, intensity, expected_rule, injected_at)
            VALUES (@id, @name, @target, @restaurants, @starts, @ends, 1, @rule, @injected)
            """,
            new { id = Guid.CreateVersion7(), name, target, restaurants, starts = T, ends = T.AddMinutes(15), rule, injected = DateTimeOffset.UtcNow.AddSeconds(-60) });

    private static Task Alert(NpgsqlConnection c, string rule, string entity, string group, DateTimeOffset episode) =>
        c.ExecuteAsync(
            """
            INSERT INTO alerts (alert_key, rule_id, rule_version, severity, entity_type, entity_key, group_key, status, episode_start,
                                title, detail, observed, first_detected_at, last_candidate_at)
            VALUES (@key, @rule, 1, 'high', 'x', @entity, @group, 'firing', @episode, 't', 'd', '{}', now(), now())
            """,
            new { key = $"{rule}:1:{entity}:{episode:O}", rule, entity, group, episode });

    [Fact]
    public async Task Alerts_are_graded_against_injected_scenarios()
    {
        await using var db = await postgres.CreateMigratedDatabaseAsync();
        await using var c = await db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await Scenario(c, "kitchen-meltdown", "r-017", ["r-017"], "restaurant-delay-burst");
        await Scenario(c, "rider-shortage", "z-04", ["r-031", "r-032"], "stuck-pickup");
        await Scenario(c, "slow-kitchen", "r-005", ["r-005"], null);

        await Alert(c, "restaurant-delay-burst", "r-017", "z-02", T.AddMinutes(-5));   // true positive
        await Alert(c, "restaurant-delay-burst", "r-017", "z-02", T.AddMinutes(200));  // right place, far too late → false
        await Alert(c, "stuck-pickup", "o-1", "r-032", T.AddMinutes(25));              // true positive (grouped under r-032)
        await Alert(c, "restaurant-delay-burst", "r-031", "z-04", T.AddMinutes(5));    // wrong rule for that scenario → false
        await Alert(c, "restaurant-delay-burst", "r-005", "z-01", T.AddMinutes(10));   // negative control fooled → false
        await Alert(c, "zone-cancellation-spike", "z-06", "z-06", T);                  // nothing injected there → false

        var grades = (await c.QueryAsync<(string Rule, long Tp, long Fp)>(
            """
            SELECT rule_id, count(*) FILTER (WHERE true_positive), count(*) FILTER (WHERE NOT true_positive)
            FROM alert_classification GROUP BY rule_id
            """)).ToDictionary(g => g.Rule, g => (g.Tp, g.Fp));

        Assert.Equal((1L, 3L), grades["restaurant-delay-burst"]);
        Assert.Equal((1L, 0L), grades["stuck-pickup"]);
        Assert.Equal((0L, 1L), grades["zone-cancellation-spike"]);

        var outcomes = (await c.QueryAsync<(string Name, long Matching, long Other, double? Seconds)>(
            "SELECT name, matching_alerts, other_alerts, seconds_to_detect::double precision FROM scenario_outcomes")).ToDictionary(o => o.Name);

        Assert.Equal(1, outcomes["kitchen-meltdown"].Matching);
        Assert.InRange(outcomes["kitchen-meltdown"].Seconds!.Value, 50, 70);
        Assert.Equal((1L, 1L), (outcomes["rider-shortage"].Matching, outcomes["rider-shortage"].Other));
        Assert.Equal((0L, 1L), (outcomes["slow-kitchen"].Matching, outcomes["slow-kitchen"].Other));
        Assert.Null(outcomes["slow-kitchen"].Seconds);
    }
}
