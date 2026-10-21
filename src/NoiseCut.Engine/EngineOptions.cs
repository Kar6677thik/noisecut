using NoiseCut.Core.Evaluation;

namespace NoiseCut.Engine;

public sealed class EngineOptions
{
    public const string Section = "Engine";

    public StateMode StateMode { get; set; } = StateMode.InMemory;

    public TimeMode TimeMode { get; set; } = TimeMode.ProcessingTime;

    public EmitMode EmitMode { get; set; } = EmitMode.EveryMatch;

    /// <summary>
    /// false (naive, M1): one engine per process holds every rule's state across all its partitions.
    /// true (robust, M3): one engine per partition (so state can move with the partition), and zone rules run in a separate
    /// zone stage fed by <c>zone-partials</c> (ADR-0004).
    /// </summary>
    public bool ZoneStage { get; set; }

    /// <summary>Must match the simulator's. Converts wall-clock time into sim-clock time in processing-time mode.</summary>
    public double TimeScale { get; set; } = 10;

    public string GroupId { get; set; } = "noisecut-engine";

    public string ZoneGroupId { get; set; } = "noisecut-engine-zone";

    /// <summary>Where a brand-new consumer group starts in naive mode. (Checkpointed mode starts from the checkpoint, or the beginning.)</summary>
    public string AutoOffsetReset { get; set; } = "Latest";

    public int AutoCommitIntervalMs { get; set; } = 5000;

    /// <summary>Checkpoint at least this often (wall clock)…</summary>
    public TimeSpan CheckpointInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>…or after this many records, whichever comes first (SPEC §6.3).</summary>
    public int CheckpointEveryRecords { get; set; } = 2000;

    /// <summary>Event time: how far the watermark trails the newest event (SPEC §6.1).</summary>
    public TimeSpan MaxOutOfOrderness { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Event time: how far behind the watermark an event may be and still be applied.</summary>
    public TimeSpan AllowedLateness { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan RuleRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan BaselineRefreshInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Unique per process; shows up in candidates (engine.instance) and in Kafka's group membership.</summary>
    public string InstanceId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";

    /// <summary>The evaluation settings this configuration implies.</summary>
    public EvaluationSettings EvaluationSettings() => TimeMode == TimeMode.EventTime
        ? Core.Evaluation.EvaluationSettings.EventTime(EmitMode) with { MaxOutOfOrderness = MaxOutOfOrderness, AllowedLateness = AllowedLateness }
        : new Core.Evaluation.EvaluationSettings { EmitMode = EmitMode };

    /// <summary>
    /// The M1 single-state worker exists to reproduce the naive failures. It does not do robust state or time:
    /// those need per-partition state, which is what <see cref="ZoneStage"/> = true provides. Refuse the combination instead
    /// of silently running in a mode the operator didn't ask for.
    /// </summary>
    public void EnsureSupportedBySingleWorker()
    {
        if (StateMode != StateMode.InMemory || TimeMode != TimeMode.ProcessingTime)
        {
            throw new NotSupportedException(
                $"StateMode={StateMode}/TimeMode={TimeMode} need the partitioned engine. Set Engine:ZoneStage=true (the robust profile does).");
        }
    }
}
