using System.Globalization;
using NoiseCut.Core.Domain;

namespace NoiseCut.Api.Pages;

/// <summary>Display helpers shared by the pages.</summary>
public static class Fmt
{
    /// <summary>Wall-clock time (when NoiseCut did something), UTC.</summary>
    public static string Wall(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Wall(DateTimeOffset? t) => t is { } v ? Wall(v) : "—";

    /// <summary>Sim-clock time (when something happened in the simulated city), shown in its local time zone.</summary>
    public static string Sim(DateTimeOffset t) =>
        t.ToOffset(Core.Simulation.DemandModel.LocalOffset).ToString("ddd HH:mm", CultureInfo.InvariantCulture) + " IST";

    public static string Entity(string key) => Topology.DisplayName(key) is var name && name != key ? $"{name} ({key})" : key;

    public static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
