using Dapper;
using Npgsql;
using NoiseCut.Core.Evaluation;

namespace NoiseCut.Infrastructure.Postgres;

/// <summary>
/// An in-memory copy of <c>zone_hourly_stats</c>, so rule B can ask for a baseline without a database round trip per evaluation.
///
/// Lookup: the exact hour <c>offset</c> ago; if that hour is missing, step back a week at a time to the most recent hour with the
/// same hour-of-week. The sim clock runs faster than the wall clock, so it soon outruns the seeded week. Falling back to
/// "the latest known same hour-of-week" keeps the rule meaningful until live statistics exist.
/// </summary>
public sealed class BaselineCache(NpgsqlDataSource db) : IBaselineSource
{
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private volatile Snapshot _snapshot = new(new Dictionary<(string, DateTimeOffset), double>(), DateTimeOffset.MaxValue);

    public int Count => _snapshot.Rates.Count;

    public async Task RefreshAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<(string ZoneId, DateTime HourStart, int Placed, int Cancelled)>(
            new CommandDefinition("SELECT zone_id, hour_start, placed, cancelled FROM zone_hourly_stats WHERE placed > 0", cancellationToken: ct));

        var rates = new Dictionary<(string, DateTimeOffset), double>();
        var earliest = DateTimeOffset.MaxValue;
        foreach (var row in rows)
        {
            var hour = new DateTimeOffset(DateTime.SpecifyKind(row.HourStart, DateTimeKind.Utc));
            rates[(row.ZoneId, hour)] = (double)row.Cancelled / row.Placed;
            earliest = hour < earliest ? hour : earliest;
        }

        _snapshot = new Snapshot(rates, earliest);
    }

    public double? GetRate(string zoneId, DateTimeOffset hourStart)
    {
        // Stepping back a week at a time keeps the hour-of-week fixed (Tuesday 13:00 stays Tuesday 13:00).
        var snapshot = _snapshot;
        for (var t = hourStart; t >= snapshot.Earliest; t -= Week)
        {
            if (snapshot.Rates.TryGetValue((zoneId, t), out var rate))
            {
                return rate;
            }
        }

        return null;
    }

    private sealed record Snapshot(IReadOnlyDictionary<(string, DateTimeOffset), double> Rates, DateTimeOffset Earliest);
}
