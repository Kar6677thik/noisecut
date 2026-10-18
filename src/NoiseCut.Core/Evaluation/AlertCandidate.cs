using NoiseCut.Core.Rules;

namespace NoiseCut.Core.Evaluation;

public enum CandidateStatus { Firing, Resolved }

/// <summary>
/// What the engine publishes to <c>alert-candidates</c> (SPEC §6.6).
/// A candidate says "rule X changed state (or still holds) for entity Y". The notifier decides whether a human hears about it.
/// </summary>
public sealed record AlertCandidate
{
    public required Guid CandidateId { get; init; }

    /// <summary><c>ruleId:ruleVersion:entityKey:episodeStart</c>. Deterministic: replaying the same records yields the same key.</summary>
    public required string AlertKey { get; init; }

    public required CandidateStatus Status { get; init; }

    public required string RuleId { get; init; }

    public required int RuleVersion { get; init; }

    public required Severity Severity { get; init; }

    public required string EntityType { get; init; }

    public required string EntityKey { get; init; }

    public required string GroupKey { get; init; }

    public required DateTimeOffset EpisodeStart { get; init; }

    public DateTimeOffset? WindowStart { get; init; }

    public DateTimeOffset? WindowEnd { get; init; }

    public IReadOnlyDictionary<string, double> Observed { get; init; } = new Dictionary<string, double>();

    public required Threshold Threshold { get; init; }

    public IReadOnlyList<Guid> SampleEventIds { get; init; } = [];

    /// <summary>The record (event or tick) whose processing made this candidate happen.</summary>
    public Guid? TriggerEventId { get; init; }

    /// <summary>Wall-clock time the trigger record was produced (Kafka CreateTime). Start of the detection-latency stopwatch.</summary>
    public required DateTimeOffset TriggerProducedAt { get; init; }

    /// <summary>Wall-clock time the engine decided. <c>DetectedAt − TriggerProducedAt</c> is detection latency (SPEC §10).</summary>
    public required DateTimeOffset DetectedAt { get; init; }

    public required EngineInfo Engine { get; init; }
}

public sealed record Threshold(string Op, double Value);

public sealed record EngineInfo(string Instance, string Stage, int Partition, long Offset);

public static class AlertKeys
{
    public static string For(Rule rule, string entityKey, DateTimeOffset episodeStart) =>
        $"{rule.Id}:{rule.Version}:{entityKey}:{episodeStart.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
}
