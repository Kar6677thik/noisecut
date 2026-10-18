using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;

namespace NoiseCut.Core.Evaluation;

/// <summary>
/// One running instance of one rule version, holding that rule's window state.
/// The engine calls <see cref="OnEvent"/> for every order event and <see cref="OnWatermark"/> whenever the watermark moves.
/// </summary>
public interface IRuleEvaluator
{
    Rule Rule { get; }

    /// <param name="time">The time used for window assignment: <c>occurredAt</c> in event-time mode, "now" in processing-time mode.</param>
    void OnEvent(OrderEvent evt, DateTimeOffset time, EvaluationContext ctx);

    void OnWatermark(DateTimeOffset watermark, EvaluationContext ctx);
}

public static class RuleEvaluators
{
    public static IRuleEvaluator Create(Rule rule, IBaselineSource baselines) => rule.Definition.Kind switch
    {
        RuleKind.WindowedCount => new WindowedCountEvaluator(rule),
        RuleKind.RatioVsBaseline => new RatioVsBaselineEvaluator(rule, baselines),
        RuleKind.Absence => new AbsenceEvaluator(rule),
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Definition.Kind, "unknown rule kind"),
    };

    internal static bool Selects(EventSelector selector, OrderEvent evt) =>
        evt.Type == selector.EventType
        && selector.Where.All(p => EventFields.Matches(evt, p.Field, p.Op, p.Value));

    internal static string GroupKeyOf(Rule rule, OrderEvent evt, string entityKey) =>
        rule.Definition.Notify.GroupBy is { } field ? EventFields.GetString(evt, field) ?? entityKey : entityKey;

    internal static string OpName(ConditionOp op) => op switch
    {
        ConditionOp.Gt => "gt",
        ConditionOp.Gte => "gte",
        ConditionOp.GteMultiple => "gte_multiple",
        _ => op.ToString(),
    };
}
