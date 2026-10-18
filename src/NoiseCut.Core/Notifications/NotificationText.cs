using System.Globalization;
using NoiseCut.Core.Domain;
using NoiseCut.Core.Evaluation;

namespace NoiseCut.Core.Notifications;

/// <summary>Human-readable one-liners for notifications. Pure, so the wording is unit-testable.</summary>
public static class NotificationText
{
    public static string Title(AlertCandidate c, string ruleName)
    {
        var status = c.Status == CandidateStatus.Firing ? c.Severity.ToString().ToUpperInvariant() : "RESOLVED";
        var subject = Label(c.EntityKey);

        // An order id means nothing to a human; say where the order is.
        if (Topology.FindRestaurant(c.GroupKey) is { } restaurant && c.GroupKey != c.EntityKey)
        {
            subject += $" at {restaurant.Name}";
        }

        return string.Create(CultureInfo.InvariantCulture, $"[{status}] {ruleName} · {subject}");
    }

    private static string Label(string key) => Topology.DisplayName(key) is var name && name != key ? $"{name} ({key})" : key;

    public static string Detail(AlertCandidate c)
    {
        var o = c.Observed;
        if (o.TryGetValue("rate", out var rate) && o.TryGetValue("baselineRate", out var baseline))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"cancellation rate {rate * 100:0.0}% vs baseline {baseline * 100:0.0}% ({o.GetValueOrDefault("numerator"):0}/{o.GetValueOrDefault("denominator"):0} orders), threshold {c.Threshold.Value:0.##}x");
        }

        if (o.TryGetValue("waitedMinutes", out var waited))
        {
            return string.Create(CultureInfo.InvariantCulture, $"not picked up {waited:0.#} min after food was ready (threshold {c.Threshold.Value:0} min)");
        }

        if (o.TryGetValue("count", out var count))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{count:0} matching orders in the window (threshold {c.Threshold.Op} {c.Threshold.Value:0})");
        }

        return c.Status.ToString();
    }
}
