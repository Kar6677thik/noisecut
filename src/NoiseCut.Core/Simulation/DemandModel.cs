namespace NoiseCut.Core.Simulation;

/// <summary>
/// The simulator's model of "normal" (SPEC §9). Shared with the Migrator, which seeds a week of hourly baselines from the same
/// curve. That way "same hour last week" (rule B) compares live traffic against history produced by the same model.
/// </summary>
public static class DemandModel
{
    /// <summary>Bengaluru local time (UTC+05:30). Lunch and dinner peaks are local-time facts.</summary>
    public static readonly TimeSpan LocalOffset = TimeSpan.FromHours(5.5);

    /// <summary>Fraction of the peak order rate at this sim time: 1.0 at lunch (11–15) and dinner (19–23).</summary>
    public static double DemandFactor(DateTimeOffset simTime) => simTime.ToOffset(LocalOffset).Hour switch
    {
        < 7 => 0.2,
        < 11 => 0.4,
        < 15 => 1.0,
        < 19 => 0.5,
        < 23 => 1.0,
        _ => 0.4,
    };

    /// <summary>Probability that an order is cancelled at some point. About 3%, slightly higher when kitchens are busy.</summary>
    public static double BaseCancelRate(DateTimeOffset simTime) => DemandFactor(simTime) >= 1.0 ? 0.035 : 0.025;
}

public sealed record HourlyStat(string ZoneId, DateTimeOffset HourStart, int Placed, int Cancelled);

/// <summary>Synthetic history for <c>zone_hourly_stats</c> (SPEC §5.3): aggregates, not replayed events.</summary>
public static class BaselineSeeder
{
    public static IReadOnlyList<HourlyStat> Generate(DateTimeOffset untilExclusive, int days, double ordersPerSimMinute, int seed)
    {
        var rng = new Random(seed);
        var end = Time.Panes.Floor(untilExclusive, TimeSpan.FromHours(1));
        var rows = new List<HourlyStat>(days * 24 * Domain.Topology.Zones.Count);

        for (var hour = end - TimeSpan.FromDays(days); hour < end; hour += TimeSpan.FromHours(1))
        {
            var expectedPerZone = ordersPerSimMinute * DemandModel.DemandFactor(hour) * 60 / Domain.Topology.Zones.Count;
            var cancelRate = DemandModel.BaseCancelRate(hour);
            foreach (var zone in Domain.Topology.Zones)
            {
                var placed = (int)Math.Round(expectedPerZone * (0.9 + (0.2 * rng.NextDouble())));
                var cancelled = 0;
                for (var i = 0; i < placed; i++)
                {
                    if (rng.NextDouble() < cancelRate)
                    {
                        cancelled++;
                    }
                }

                rows.Add(new HourlyStat(zone.Id, hour, placed, cancelled));
            }
        }

        return rows;
    }
}
