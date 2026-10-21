namespace NoiseCut.Core.Evaluation;

/// <summary>
/// One order-stage partition's view of one zone-hour, published to <c>zone-partials</c> (SPEC §6.4, ADR-0004).
///
/// The counts are <b>cumulative</b> (everything this source partition has seen for that zone and hour), not deltas.
/// That makes the message idempotent: replaying it, receiving it twice, or receiving an older one after a newer one
/// can only ever leave the zone stage with the latest totals. <see cref="Seq"/> tells "newer" from "older".
/// </summary>
public sealed record ZonePartial
{
    public required string RuleId { get; init; }

    public required int RuleVersion { get; init; }

    public required string ZoneId { get; init; }

    public required DateTimeOffset HourStart { get; init; }

    public required int SourcePartition { get; init; }

    public required long Numerator { get; init; }

    public required long Denominator { get; init; }

    /// <summary>The source partition's watermark when this was published. The zone stage's watermark is the minimum of these.</summary>
    public required DateTimeOffset SourceWatermark { get; init; }

    /// <summary>Increases with every partial a source partition publishes. Part of its checkpointed state, so replays reuse the same numbers.</summary>
    public required long Seq { get; init; }

    /// <summary>When the record that caused this publish was produced (wall clock), so rule-B detection latency is end to end.</summary>
    public required DateTimeOffset TriggerProducedAt { get; init; }
}

/// <summary>A zone-hour whose counts became final (the watermark passed hour end + allowed lateness).</summary>
public sealed record ClosedHour(string RuleId, string ZoneId, DateTimeOffset HourStart, long Numerator, long Denominator);
