using System.Text.RegularExpressions;
using NoiseCut.Core.Events;
using NoiseCut.Core.Time;

namespace NoiseCut.Core.Rules;

/// <summary>
/// Enforces SPEC §5.5. The Api runs this before saving a rule and the engine runs it again on load,
/// so a bad rule is rejected loudly instead of silently never firing.
/// </summary>
public static partial class RuleValidator
{
    public static readonly IReadOnlySet<string> KnownChannels =
        new HashSet<string>(StringComparer.Ordinal) { "dashboard", "webhook", "email" };

    private static readonly HashSet<string> Entities = new(StringComparer.Ordinal) { "restaurantId", "zoneId", "orderId" };

    [GeneratedRegex("^[a-z0-9-]{3,64}$")]
    private static partial Regex IdPattern();

    public static IReadOnlyList<string> Validate(RuleDefinition rule) => Validate(rule, KnownChannels);

    public static IReadOnlyList<string> Validate(RuleDefinition rule, IReadOnlySet<string> channels)
    {
        var errors = new List<string>();

        if (!IdPattern().IsMatch(rule.Id))
        {
            errors.Add("id: must match ^[a-z0-9-]{3,64}$");
        }

        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            errors.Add("name: required");
        }

        if (!Entities.Contains(rule.Entity))
        {
            errors.Add("entity: must be restaurantId, zoneId or orderId");
        }

        ValidateNotify(rule.Notify, channels, errors);

        switch (rule.Kind)
        {
            case RuleKind.WindowedCount:
                ValidateWindowedCount(rule, errors);
                break;
            case RuleKind.RatioVsBaseline:
                ValidateRatio(rule, errors);
                break;
            case RuleKind.Absence:
                ValidateAbsence(rule, errors);
                break;
        }

        return errors;
    }

    private static void ValidateWindowedCount(RuleDefinition rule, List<string> errors)
    {
        ValidateSelector("source", rule.Source, errors);

        if (rule.DistinctBy is not null && !IsKnownField(rule.DistinctBy, rule.Source?.EventType))
        {
            errors.Add($"distinctBy: unknown field '{rule.DistinctBy}'");
        }

        if (rule.Window is not { } w)
        {
            errors.Add("window: required");
        }
        else
        {
            RequireWholePanes("window.size", w.Size, errors);
            if (w.Type == WindowType.Sliding)
            {
                if (w.Slide is not { } slide)
                {
                    errors.Add("window.slide: required for sliding windows");
                }
                else
                {
                    RequireWholePanes("window.slide", slide, errors);
                    if (slide > TimeSpan.Zero && w.Size.Ticks % slide.Ticks != 0)
                    {
                        errors.Add("window: size must be a multiple of slide");
                    }
                }
            }
        }

        // Only monotonic conditions ("count can only grow") are allowed, because they let us fire early (SPEC §5.2).
        if (rule.Condition is not { } c)
        {
            errors.Add("condition: required");
        }
        else if (c.Op is not (ConditionOp.Gt or ConditionOp.Gte) || c.Value < 1)
        {
            errors.Add("condition: windowed_count supports gt/gte with a value of at least 1");
        }
    }

    private static void ValidateRatio(RuleDefinition rule, List<string> errors)
    {
        ValidateSelector("numerator", rule.Numerator, errors);
        ValidateSelector("denominator", rule.Denominator, errors);

        if (rule.Entity != "zoneId")
        {
            errors.Add("entity: ratio_vs_baseline is per zone (baselines are stored per zone and hour)");
        }

        if (rule.Window is not { Type: WindowType.Tumbling } w || w.Size != TimeSpan.FromHours(1))
        {
            errors.Add("window: ratio_vs_baseline needs a tumbling PT1H window (baselines are hourly)");
        }

        if (rule.Baseline is not { } b)
        {
            errors.Add("baseline: required");
        }
        else
        {
            if (b.Offset <= TimeSpan.Zero || b.Offset.Ticks % TimeSpan.TicksPerHour != 0)
            {
                errors.Add("baseline.offset: must be a positive whole number of hours");
            }

            if (b.Source != "zone_hourly_stats")
            {
                errors.Add("baseline.source: only zone_hourly_stats is supported");
            }
        }

        if (rule.Condition is not { Op: ConditionOp.GteMultiple, Value: > 0 })
        {
            errors.Add("condition: ratio_vs_baseline needs op gte_multiple with a positive value");
        }

        if (rule.Guards is { } g && (g.MinDenominator < 1 || g.MinBaselineRate < 0 || g.MinBaselineRate >= 1))
        {
            errors.Add("guards: minDenominator ≥ 1 and 0 ≤ minBaselineRate < 1");
        }
    }

    private static void ValidateAbsence(RuleDefinition rule, List<string> errors)
    {
        ValidateSelector("trigger", rule.Trigger, errors);
        ValidateSelector("expected", rule.Expected, errors);

        foreach (var type in rule.CancelIf ?? [])
        {
            if (!EventTypes.IsOrderEvent(type))
            {
                errors.Add($"cancelIf: unknown event type '{type}'");
            }
        }

        if (rule.CorrelateBy != "orderId" || rule.Entity != "orderId")
        {
            errors.Add("absence: correlateBy and entity must both be orderId");
        }

        if (rule.Timeout is not { } t || t <= TimeSpan.Zero)
        {
            errors.Add("timeout: required and positive");
        }
    }

    private static void ValidateSelector(string path, EventSelector? selector, List<string> errors)
    {
        if (selector is null)
        {
            errors.Add($"{path}: required");
            return;
        }

        if (!EventTypes.IsOrderEvent(selector.EventType))
        {
            errors.Add($"{path}.eventType: unknown event type '{selector.EventType}'");
            return;
        }

        for (var i = 0; i < selector.Where.Count; i++)
        {
            if (!IsKnownField(selector.Where[i].Field, selector.EventType))
            {
                errors.Add($"{path}.where[{i}].field: '{selector.Where[i].Field}' does not exist on {selector.EventType}");
            }
        }
    }

    private static bool IsKnownField(string field, string? eventType)
    {
        if (EventTypes.EnvelopeFields.Contains(field))
        {
            return true;
        }

        return field.StartsWith("data.", StringComparison.Ordinal)
            && eventType is not null
            && EventTypes.DataFields.TryGetValue(eventType, out var fields)
            && fields.Contains(field["data.".Length..]);
    }

    private static void RequireWholePanes(string path, TimeSpan value, List<string> errors)
    {
        if (value <= TimeSpan.Zero || value.Ticks % Panes.Size.Ticks != 0)
        {
            errors.Add($"{path}: must be a positive whole number of minutes (1-minute panes)");
        }
    }

    private static void ValidateNotify(NotifySpec notify, IReadOnlySet<string> channels, List<string> errors)
    {
        if (notify.Channels.Count == 0)
        {
            errors.Add("notify.channels: at least one channel");
        }

        foreach (var channel in notify.Channels.Where(c => !channels.Contains(c)))
        {
            errors.Add($"notify.channels: unknown channel '{channel}'");
        }

        if (notify.Cooldown is { } cd && cd < TimeSpan.Zero)
        {
            errors.Add("notify.cooldown: must not be negative");
        }

        if (notify.GroupWait is { } gw && gw < TimeSpan.Zero)
        {
            errors.Add("notify.groupWait: must not be negative");
        }

        if (notify.GroupBy is not null && !EventTypes.EnvelopeFields.Contains(notify.GroupBy))
        {
            errors.Add("notify.groupBy: must be an envelope field such as zoneId or restaurantId");
        }

        var previous = TimeSpan.Zero;
        for (var i = 0; i < notify.Escalation.Count; i++)
        {
            var step = notify.Escalation[i];
            if (step.After <= previous)
            {
                errors.Add($"notify.escalation[{i}].after: steps must be strictly increasing and positive");
            }

            if (step.Channels.Count == 0 || step.Channels.Any(c => !channels.Contains(c)))
            {
                errors.Add($"notify.escalation[{i}].channels: must be non-empty known channels");
            }

            previous = step.After;
        }
    }
}
