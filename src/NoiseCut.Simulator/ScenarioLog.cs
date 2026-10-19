using Dapper;
using Npgsql;
using NoiseCut.Core.Simulation;

namespace NoiseCut.Simulator;

/// <summary>
/// The truth log (M2): every scenario the simulator injects is written to <c>sim_scenarios</c>, so experiments can compare
/// what NoiseCut alerted on with what actually happened. Optional: without a connection string, nothing is recorded.
/// </summary>
public sealed class ScenarioLog(NpgsqlDataSource? db, ILogger<ScenarioLog> logger)
{
    public async Task RecordAsync(ActiveScenario s, CancellationToken ct)
    {
        if (db is null)
        {
            return;
        }

        try
        {
            await using var conn = await db.OpenConnectionAsync(ct);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO sim_scenarios (id, name, target, restaurants, starts_at, ends_at, intensity, expected_rule)
                VALUES (@Id, @Name, @Target, @Restaurants, @StartsAt, @EndsAt, @Intensity, @ExpectedRule)
                """,
                new { s.Id, s.Name, s.Target, Restaurants = s.Restaurants.ToArray(), s.StartsAt, s.EndsAt, s.Intensity, s.ExpectedRule },
                cancellationToken: ct));
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // The simulation must keep running even if the truth log is down; the experiment will notice the gap.
            logger.LogError(ex, "Could not record scenario {Id} in the truth log", s.Id);
        }
    }
}
