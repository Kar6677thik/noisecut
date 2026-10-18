using NoiseCut.Core.Rules;

namespace NoiseCut.Core.Evaluation;

/// <summary>Facts about the Kafka record currently being processed. Stamped onto every candidate it causes.</summary>
public sealed record RecordInfo(Guid? EventId, DateTimeOffset ProducedAt, int Partition, long Offset);

/// <summary>
/// Everything an evaluator may touch while handling one record: the current watermark, the emit mode,
/// and an output list. Evaluators never talk to Kafka or the clock directly, which keeps them deterministic and testable.
/// </summary>
public sealed class EvaluationContext(EvaluationSettings settings, TimeProvider wallClock, string instance, string stage)
{
    private readonly List<AlertCandidate> _output = [];

    public EvaluationSettings Settings { get; } = settings;

    public DateTimeOffset Watermark { get; internal set; } = DateTimeOffset.MinValue;

    internal RecordInfo Record { get; set; } = new(null, DateTimeOffset.MinValue, -1, -1);

    public IReadOnlyList<AlertCandidate> Output => _output;

    internal void Clear() => _output.Clear();

    public void Emit(
        Rule rule,
        CandidateStatus status,
        string entityKey,
        string groupKey,
        DateTimeOffset episodeStart,
        Threshold threshold,
        IReadOnlyDictionary<string, double> observed,
        DateTimeOffset? windowStart = null,
        DateTimeOffset? windowEnd = null,
        IReadOnlyList<Guid>? samples = null)
    {
        _output.Add(new AlertCandidate
        {
            CandidateId = Guid.CreateVersion7(),
            AlertKey = AlertKeys.For(rule, entityKey, episodeStart),
            Status = status,
            RuleId = rule.Id,
            RuleVersion = rule.Version,
            Severity = rule.Definition.Severity,
            EntityType = rule.Definition.Entity,
            EntityKey = entityKey,
            GroupKey = groupKey,
            EpisodeStart = episodeStart,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            Observed = observed,
            Threshold = threshold,
            SampleEventIds = samples ?? [],
            TriggerEventId = Record.EventId,
            TriggerProducedAt = Record.ProducedAt,
            DetectedAt = wallClock.GetUtcNow(),
            Engine = new EngineInfo(instance, stage, Record.Partition, Record.Offset),
        });
    }
}
