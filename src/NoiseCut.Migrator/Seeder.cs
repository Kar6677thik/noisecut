using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using NoiseCut.Core.Domain;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Simulation;
using NoiseCut.Infrastructure.Postgres;

namespace NoiseCut.Migrator;

/// <summary>
/// Seeds reference data, the three reference rules and a week of baseline statistics. Idempotent: safe to run on every deploy.
/// </summary>
public sealed class Seeder(NpgsqlDataSource db, TimeProvider clock, ILogger logger)
{
    public sealed record Options(double OrdersPerSimMinute, int Seed, int BaselineDays);

    public async Task RunAsync(Options options, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Reference data always matches the code (upsert), because the simulator has the same table compiled in.
        await conn.ExecuteAsync(
            "INSERT INTO zones (id, name) VALUES (@Id, @Name) ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name",
            Topology.Zones,
            tx);
        await conn.ExecuteAsync(
            """
            INSERT INTO restaurants (id, name, zone_id) VALUES (@Id, @Name, @ZoneId)
            ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, zone_id = EXCLUDED.zone_id
            """,
            Topology.Restaurants,
            tx);

        foreach (var rule in ReferenceRules.All)
        {
            if (await RuleStore.InsertIfMissingAsync(conn, tx, rule))
            {
                logger.LogInformation("Seeded rule {RuleId}", rule.Id);
            }
        }

        // Baselines are seeded once. Later runs must not overwrite live statistics.
        var existing = await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM zone_hourly_stats", transaction: tx);
        if (existing == 0)
        {
            var rows = BaselineSeeder.Generate(clock.GetUtcNow(), options.BaselineDays, options.OrdersPerSimMinute, options.Seed);
            await conn.ExecuteAsync(
                """
                INSERT INTO zone_hourly_stats (zone_id, hour_start, placed, cancelled, source)
                VALUES (@ZoneId, @HourStart, @Placed, @Cancelled, 'seed')
                ON CONFLICT DO NOTHING
                """,
                rows,
                tx);
            logger.LogInformation("Seeded {Count} baseline rows ({Days} days x {Zones} zones)", rows.Count, options.BaselineDays, Topology.Zones.Count);
        }

        await tx.CommitAsync(ct);
    }
}
