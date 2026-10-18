namespace NoiseCut.Core.Evaluation;

/// <summary>Which clock assigns records to windows (SPEC §6.2).</summary>
public enum TimeMode { ProcessingTime, EventTime }

/// <summary>Where window state lives (SPEC §6.3).</summary>
public enum StateMode { InMemory, Checkpointed }

/// <summary>When candidates are emitted (SPEC §6.5).</summary>
public enum EmitMode { EveryMatch, OnTransition }

public sealed record EvaluationSettings
{
    public EmitMode EmitMode { get; init; } = EmitMode.EveryMatch;

    /// <summary>
    /// How far behind the newest record the watermark trails. Zero in processing-time mode:
    /// "now" never goes backwards, so there is nothing to wait for.
    /// </summary>
    public TimeSpan MaxOutOfOrderness { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// How long a stuck-pickup alert is remembered while waiting for the pickup that would resolve it.
    /// Bounds memory if a pickup never arrives (SPEC §5.4).
    /// </summary>
    public TimeSpan MaxOrderAge { get; init; } = TimeSpan.FromHours(3);
}
