namespace NoiseCut.Core.Evaluation;

/// <summary>Which clock assigns records to windows (SPEC §6.2).</summary>
public enum TimeMode { ProcessingTime, EventTime }

/// <summary>Where window state lives (SPEC §6.3).</summary>
public enum StateMode { InMemory, Checkpointed }

/// <summary>When candidates are emitted (SPEC §6.5).</summary>
public enum EmitMode { EveryMatch, OnTransition }

/// <summary>What a <see cref="RuleEngine"/> instance is responsible for.</summary>
public enum EngineRole
{
    /// <summary>M1 / naive: one engine sees every partition it owns and evaluates every rule, zone rules included.</summary>
    Single,

    /// <summary>Robust order stage: one engine per partition. Zone rules only <i>count</i>, and publish partials (ADR-0004).</summary>
    Order,
}

public enum Lateness
{
    /// <summary>At or after the watermark: normal.</summary>
    OnTime,

    /// <summary>Behind the watermark but within AllowedLateness: still applied.</summary>
    Late,

    /// <summary>Further behind than AllowedLateness: not applied, routed to <c>late-events</c>.</summary>
    TooLate,
}

public sealed record EvaluationSettings
{
    public EmitMode EmitMode { get; init; } = EmitMode.EveryMatch;

    /// <summary>
    /// How far behind the newest record the watermark trails. Zero in processing-time mode:
    /// "now" never goes backwards, so there is nothing to wait for. 30 s in event-time mode (SPEC §6.1).
    /// </summary>
    public TimeSpan MaxOutOfOrderness { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// How far behind the watermark an event may be and still be applied (SPEC §6.1). State is kept that much longer
    /// before it is discarded, and hour-level results are final only after it has passed.
    /// </summary>
    public TimeSpan AllowedLateness { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Absence rules remember an "expected" event that arrives <i>before</i> its trigger, so the late trigger never arms
    /// a timer (SPEC §5.4). Off in the naive profile to reproduce Break #2.
    /// </summary>
    public bool OrderInsensitiveAbsence { get; init; }

    /// <summary>
    /// How long a stuck-pickup alert is remembered while waiting for the pickup that would resolve it.
    /// Bounds memory if a pickup never arrives (SPEC §5.4).
    /// </summary>
    public TimeSpan MaxOrderAge { get; init; } = TimeSpan.FromHours(3);

    /// <summary>The robust event-time settings: watermark 30 s behind, 2 minutes of allowed lateness, order-insensitive absence.</summary>
    public static EvaluationSettings EventTime(EmitMode emitMode) => new()
    {
        EmitMode = emitMode,
        MaxOutOfOrderness = TimeSpan.FromSeconds(30),
        AllowedLateness = TimeSpan.FromMinutes(2),
        OrderInsensitiveAbsence = true,
    };
}
