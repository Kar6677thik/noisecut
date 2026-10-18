using NoiseCut.Core.Evaluation;

namespace NoiseCut.Engine;

public sealed class EngineOptions
{
    public const string Section = "Engine";

    public StateMode StateMode { get; set; } = StateMode.InMemory;

    public TimeMode TimeMode { get; set; } = TimeMode.ProcessingTime;

    public EmitMode EmitMode { get; set; } = EmitMode.EveryMatch;

    /// <summary>Must match the simulator's. Converts wall-clock time into sim-clock time in processing-time mode.</summary>
    public double TimeScale { get; set; } = 10;

    public string GroupId { get; set; } = "noisecut-engine";

    /// <summary>Where a brand-new consumer group starts. "Latest" avoids replaying days of history into processing-time windows.</summary>
    public string AutoOffsetReset { get; set; } = "Latest";

    public int AutoCommitIntervalMs { get; set; } = 5000;

    public TimeSpan RuleRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan BaselineRefreshInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Unique per process; shows up in candidates (engine.instance) and in Kafka's group membership.</summary>
    public string InstanceId { get; set; } = $"{Environment.MachineName}-{Environment.ProcessId}";

    /// <summary>
    /// M1 implements the naive modes only. Robust state and time modes arrive in M3, together with the experiments that justify them.
    /// Failing fast beats silently running in a mode that is not what the operator asked for.
    /// </summary>
    public void EnsureSupported()
    {
        if (StateMode != StateMode.InMemory)
        {
            throw new NotSupportedException($"Engine:StateMode={StateMode} is not implemented yet (milestone M3). Use InMemory.");
        }

        if (TimeMode != TimeMode.ProcessingTime)
        {
            throw new NotSupportedException($"Engine:TimeMode={TimeMode} is not implemented yet (milestone M3). Use ProcessingTime.");
        }
    }
}
