using NoiseCut.Core.Domain;
using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Notifications;

namespace NoiseCut.Notifier;

/// <summary>The body of one notification. Stored in <c>deliveries.payload</c> and sent as-is by the generic webhook format.</summary>
public sealed record NotificationPayload
{
    public required string NotificationKey { get; init; }

    public required string Kind { get; init; }

    public required string AlertKey { get; init; }

    public required string Status { get; init; }

    public required string RuleId { get; init; }

    public required string RuleName { get; init; }

    public required string Severity { get; init; }

    public required string EntityType { get; init; }

    public required string EntityKey { get; init; }

    public required string EntityName { get; init; }

    public required string GroupKey { get; init; }

    public required DateTimeOffset EpisodeStart { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    public required IReadOnlyDictionary<string, double> Observed { get; init; }

    public required DateTimeOffset TriggerProducedAt { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>
    /// Identity of the logical notification: same alert, same transition, same escalation step, same channel.
    /// Sent as <c>Idempotency-Key</c>. If the receiver ever sees it twice, we sent a duplicate.
    /// </summary>
    public static string KeyFor(string alertKey, string kind, int step, string channel) => $"{alertKey}|{kind}|{step}|{channel}";

    public static NotificationPayload From(AlertCandidate c, string ruleName, string channel)
    {
        var kind = c.Status == CandidateStatus.Firing ? "firing" : "resolved";
        return new NotificationPayload
        {
            NotificationKey = KeyFor(c.AlertKey, kind, 0, channel),
            Kind = kind,
            AlertKey = c.AlertKey,
            Status = kind,
            RuleId = c.RuleId,
            RuleName = ruleName,
            Severity = c.Severity.ToString().ToLowerInvariant(),
            EntityType = c.EntityType,
            EntityKey = c.EntityKey,
            EntityName = Topology.DisplayName(c.EntityKey),
            GroupKey = c.GroupKey,
            EpisodeStart = c.EpisodeStart,
            Title = NotificationText.Title(c, ruleName),
            Detail = NotificationText.Detail(c),
            Observed = c.Observed,
            TriggerProducedAt = c.TriggerProducedAt,
            DetectedAt = c.DetectedAt,
        };
    }
}
