using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Notifications;
using NoiseCut.Core.Serialization;

namespace NoiseCut.Notifier;

/// <summary>
/// SPEC §7.1: everything a candidate causes happens in <b>one</b> PostgreSQL transaction:
/// log it, update the alert, decide, and write the outbox rows. Either all of it is committed, or none of it.
/// The Kafka offset is committed only afterwards, so a crash in between replays the candidate. The intake log turns that
/// replay into a recorded repeat instead of a second alert.
/// </summary>
public sealed class CandidateIntake(NpgsqlDataSource db, RuleCatalog rules, IOptions<NotifierOptions> options, ILogger<CandidateIntake> logger)
{
    public sealed record Outcome(bool FirstTime, int DeliveriesQueued);

    public async Task<Outcome> HandleAsync(AlertCandidate c, CancellationToken ct)
    {
        var rule = rules.Find(c.RuleId);
        var ruleName = rule?.Definition.Name ?? c.RuleId;
        var status = c.Status == CandidateStatus.Firing ? "firing" : "resolved";
        var observed = JsonSerializer.Serialize(c.Observed, NoiseCutJson.Options);

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // 1. Intake log. "xmax = 0" is true only for a freshly inserted row (an updated row has a non-zero xmax),
        //    so one statement both records the candidate and tells us whether we have seen this transition before.
        var firstTime = await conn.ExecuteScalarAsync<bool>(
            """
            INSERT INTO alert_candidates (alert_key, status, candidate_id, rule_id, payload)
            VALUES (@AlertKey, @Status, @CandidateId, @RuleId, @Payload::jsonb)
            ON CONFLICT (alert_key, status) DO UPDATE
                SET repeat_count = alert_candidates.repeat_count + 1, last_repeat_at = now()
            RETURNING (xmax = 0)
            """,
            new { c.AlertKey, Status = status, c.CandidateId, c.RuleId, Payload = JsonSerializer.Serialize(c, NoiseCutJson.Options) },
            tx);

        // 2. The alert row (one per episode). A late "firing" never reopens a resolved episode.
        await conn.ExecuteAsync(
            """
            INSERT INTO alerts (alert_key, rule_id, rule_version, severity, entity_type, entity_key, group_key, status,
                                episode_start, title, detail, observed, first_detected_at, last_candidate_at, resolved_at)
            VALUES (@AlertKey, @RuleId, @RuleVersion, @Severity, @EntityType, @EntityKey, @GroupKey, @Status,
                    @EpisodeStart, @Title, @Detail, @Observed::jsonb, @DetectedAt, @DetectedAt,
                    CASE WHEN @Status = 'resolved' THEN @DetectedAt END)
            ON CONFLICT (alert_key) DO UPDATE SET
                candidate_count   = alerts.candidate_count + 1,
                last_candidate_at = EXCLUDED.last_candidate_at,
                observed          = EXCLUDED.observed,
                detail            = EXCLUDED.detail,
                status            = CASE WHEN EXCLUDED.status = 'resolved' THEN 'resolved' ELSE alerts.status END,
                resolved_at       = COALESCE(alerts.resolved_at, EXCLUDED.resolved_at)
            """,
            new
            {
                c.AlertKey,
                c.RuleId,
                c.RuleVersion,
                Severity = c.Severity.ToString().ToLowerInvariant(),
                c.EntityType,
                c.EntityKey,
                c.GroupKey,
                Status = status,
                c.EpisodeStart,
                Title = NotificationText.Title(c, ruleName),
                Detail = NotificationText.Detail(c),
                Observed = observed,
                c.DetectedAt,
            },
            tx);

        // 3. Decide. M1 policy is pass-through: every candidate becomes a notification on every channel of the rule,
        //    unless Dedup is on and this exact transition was already handled.
        var queued = 0;
        if (firstTime || !options.Value.Dedup)
        {
            var channels = (rule?.Definition.Notify.Channels ?? []).Append("dashboard").Distinct(StringComparer.Ordinal);
            foreach (var channel in channels)
            {
                var payload = NotificationPayload.From(c, ruleName, channel);
                await conn.ExecuteAsync(
                    """
                    INSERT INTO deliveries (id, notification_key, alert_key, kind, step, channel, payload)
                    VALUES (@Id, @NotificationKey, @AlertKey, @Kind, 0, @Channel, @Payload::jsonb)
                    """,
                    new
                    {
                        Id = Guid.CreateVersion7(),
                        payload.NotificationKey,
                        c.AlertKey,
                        payload.Kind,
                        Channel = channel,
                        Payload = JsonSerializer.Serialize(payload, NoiseCutJson.Options),
                    },
                    tx);
                queued++;
            }
        }
        else
        {
            logger.LogDebug("Duplicate {Status} for {AlertKey} suppressed (dedup)", status, c.AlertKey);
        }

        await tx.CommitAsync(ct);
        return new Outcome(firstTime, queued);
    }
}
