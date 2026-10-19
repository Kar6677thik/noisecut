namespace NoiseCut.Simulator;

/// <summary>
/// Chaos knobs (SPEC §9). They change <b>when</b> or <b>how often</b> an event is sent, never <c>occurredAt</c>:
/// that is exactly the gap between event time and processing time that M2 measures.
/// </summary>
public sealed record ChaosSettings
{
    /// <summary>Chance that an order event is held back before sending (out-of-order delivery).</summary>
    public double OutOfOrderProbability { get; init; }

    /// <summary>Longest hold-back, in wall seconds. Each held event waits a uniform random time up to this.</summary>
    public double MaxHoldBackSeconds { get; init; } = 30;

    /// <summary>Chance that an order event is sent twice with the same eventId (producer retry gone wrong).</summary>
    public double DuplicateProbability { get; init; }

    public static ChaosSettings Off { get; } = new();

    public string? Validate() =>
        OutOfOrderProbability is < 0 or > 1 || DuplicateProbability is < 0 or > 1 ? "probabilities must be between 0 and 1"
        : MaxHoldBackSeconds is < 0 or > 600 ? "maxHoldBackSeconds must be between 0 and 600"
        : null;
}

/// <summary>Request body for <c>POST /chaos/late-burst</c>: hold every order event for this many wall seconds, then release them together.</summary>
public sealed record LateBurstRequest(double Seconds);
