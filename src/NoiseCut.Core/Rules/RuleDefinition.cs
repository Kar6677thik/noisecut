using System.Text.Json;

namespace NoiseCut.Core.Rules;

public enum RuleKind { WindowedCount, RatioVsBaseline, Absence }

public enum Severity { Low, Medium, High, Critical }

public enum CompareOp { Gt, Gte, Lt, Lte, Eq, Ne }

public enum ConditionOp { Gt, Gte, GteMultiple }

public enum WindowType { Sliding, Tumbling }

/// <summary>
/// A rule as stored in <c>rules.definition</c> (SPEC §5). One flat shape for all three kinds.
/// Kind-specific sections are nullable, and <see cref="RuleValidator"/> checks that the right ones are present.
/// Durations are ISO-8601 in JSON. Detection durations are event time; notification durations are wall-clock time.
/// </summary>
public sealed record RuleDefinition
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public bool Enabled { get; init; } = true;

    public required RuleKind Kind { get; init; }

    public Severity Severity { get; init; } = Severity.Medium;

    /// <summary>Which event field identifies the thing being alerted on: restaurantId, zoneId or orderId.</summary>
    public required string Entity { get; init; }

    public NotifySpec Notify { get; init; } = new();

    // --- windowed_count ---
    public EventSelector? Source { get; init; }

    public string? DistinctBy { get; init; }

    public WindowSpec? Window { get; init; }

    public ConditionSpec? Condition { get; init; }

    // --- ratio_vs_baseline ---
    public EventSelector? Numerator { get; init; }

    public EventSelector? Denominator { get; init; }

    public BaselineSpec? Baseline { get; init; }

    public GuardsSpec? Guards { get; init; }

    // --- absence ---
    public EventSelector? Trigger { get; init; }

    public EventSelector? Expected { get; init; }

    public IReadOnlyList<string>? CancelIf { get; init; }

    public string? CorrelateBy { get; init; }

    public TimeSpan? Timeout { get; init; }
}

public sealed record EventSelector
{
    public required string EventType { get; init; }

    public IReadOnlyList<FieldPredicate> Where { get; init; } = [];
}

public sealed record FieldPredicate
{
    public required string Field { get; init; }

    public required CompareOp Op { get; init; }

    public required JsonElement Value { get; init; }
}

public sealed record WindowSpec
{
    public required WindowType Type { get; init; }

    public required TimeSpan Size { get; init; }

    public TimeSpan? Slide { get; init; }
}

public sealed record ConditionSpec
{
    public required ConditionOp Op { get; init; }

    public required double Value { get; init; }
}

public sealed record BaselineSpec
{
    public required TimeSpan Offset { get; init; }

    public string Source { get; init; } = "zone_hourly_stats";
}

public sealed record GuardsSpec
{
    public int MinDenominator { get; init; } = 1;

    public double MinBaselineRate { get; init; }
}

public sealed record NotifySpec
{
    public IReadOnlyList<string> Channels { get; init; } = ["dashboard"];

    public TimeSpan? Cooldown { get; init; }

    public string? GroupBy { get; init; }

    public TimeSpan? GroupWait { get; init; }

    public IReadOnlyList<EscalationStep> Escalation { get; init; } = [];
}

public sealed record EscalationStep
{
    public required TimeSpan After { get; init; }

    public required IReadOnlyList<string> Channels { get; init; }
}

/// <summary>A rule definition plus the version it was stored under. A new version always starts with fresh state.</summary>
public sealed record Rule(RuleDefinition Definition, int Version)
{
    public string Id => Definition.Id;
}
