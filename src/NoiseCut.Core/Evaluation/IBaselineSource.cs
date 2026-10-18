namespace NoiseCut.Core.Evaluation;

/// <summary>
/// Historical cancellation rates for rule B. Synchronous on purpose: evaluators run inside the hot consume loop,
/// so the implementation must answer from memory (the engine preloads <c>zone_hourly_stats</c> into a cache).
/// </summary>
public interface IBaselineSource
{
    /// <summary>cancelled/placed for the zone in the hour starting at <paramref name="hourStart"/>, or null if unknown.</summary>
    double? GetRate(string zoneId, DateTimeOffset hourStart);
}

public sealed class NoBaseline : IBaselineSource
{
    public static readonly NoBaseline Instance = new();

    public double? GetRate(string zoneId, DateTimeOffset hourStart) => null;
}
