using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Notifications;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Serialization;
using NoiseCut.Infrastructure.Telemetry;

namespace NoiseCut.Notifier;

/// <summary>
/// SPEC §7.1: everything a candidate causes happens in <b>one</b> PostgreSQL transaction:
/// log it, update the alert, decide, and write the outbox rows. Either all of it is committed, or none of it.
/// The Kafka offset is committed only afterwards, so a crash in between replays the candidate. The intake log turns that
/// replay into a recorded repeat instead of a second alert.
///
/// The decision (SPEC §7.2), each stage switchable for experiments:
/// <list type="number">
/// <item><b>Dedup</b>: this exact (alertKey, status) was handled before → record the repeat, notify nobody.</item>
/// <item><b>Cooldown</b> (firing only): the same rule + entity notified within <c>notify.cooldown</c> → count it on that earlier
///   alert as suppressed, notify nobody. Resolutions are never cooled down.</item>
/// <item><b>Grouping</b>: the rule has <c>groupBy</c> + <c>groupWait</c> → join the open group for (rule, group, kind);
///   the group flusher sends one message for all its members.</item>
/// <item>Otherwise: one delivery per channel, now.</item>
/// </list>
/// A "resolved" is only sent for an episode whose "firing" was sent (or queued): nobody needs to hear that something they
/// were never told about is over.
/// </summary>
public sealed class CandidateIntake(NpgsqlDataSource db, RuleCatalog rules, IOptions<NotifierOptions> options, TimeProvider clock, ILogger<CandidateIntake> logger)
{
    public sealed record Outcome(bool FirstTime, int DeliveriesQueued, string Decision);

    public async Task<Outcome> HandleAsync(AlertCandidate c, CancellationToken ct)
    {
        var policy = options.Value;
        var rule = rules.Find(c.RuleId);
        var ruleName = rule?.Definition.Name ?? c.RuleId;
        var status = c.Status == CandidateStatus.Firing ? "firing" : "resolved";
        var now = clock.GetUtcNow();

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

        // 2. The alert row (one per episode). A late "firing" never reopens a resolved episode; an acknowledged one stays acknowledged.
        var notified = await conn.ExecuteScalarAsync<bool>(
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
            RETURNING notified
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
                Observed = JsonSerializer.Serialize(c.Observed, NoiseCutJson.Options),
                c.DetectedAt,
            },
            tx);

        // 3. Decide.
        var (queued, decision) = await DecideAsync(conn, tx, c, rule, ruleName, firstTime, notified, now, policy);
        await tx.CommitAsync(ct);

        if (decision != "notified")
        {
            // dedup, cooldown, unnotified, or grouped (merged into a group someone else opened)
            NoiseCutMetrics.Suppressed.Add(1, new("rule", c.RuleId), new("reason", decision));
        }

        logger.LogDebug("{Status} {AlertKey}: {Decision}", status, c.AlertKey, decision);
        return new Outcome(firstTime, queued, decision);
    }

    private static async Task<(int Queued, string Decision)> DecideAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, AlertCandidate c, Rule? rule, string ruleName,
        bool firstTime, bool notified, DateTimeOffset now, NotifierOptions policy)
    {
        var notify = rule?.Definition.Notify ?? new NotifySpec();
        var kind = c.Status == CandidateStatus.Firing ? "firing" : "resolved";

        if (!firstTime && policy.Dedup)
        {
            return (0, "dedup");
        }

        if (c.Status == CandidateStatus.Resolved && (policy.Cooldown || policy.Grouping) && !notified)
        {
            return (0, "unnotified");
        }

        if (c.Status == CandidateStatus.Firing && policy.Cooldown && notify.Cooldown is { } cooldown && cooldown > TimeSpan.Zero)
        {
            var holder = await conn.QuerySingleOrDefaultAsync<(string AlertKey, DateTime NotifiedAt)?>(
                "SELECT alert_key, notified_at FROM cooldowns WHERE rule_id = @RuleId AND entity_key = @EntityKey FOR UPDATE",
                new { c.RuleId, c.EntityKey },
                tx);
            if (holder is { } h && h.AlertKey != c.AlertKey && new DateTimeOffset(DateTime.SpecifyKind(h.NotifiedAt, DateTimeKind.Utc)) > now - cooldown)
            {
                await conn.ExecuteAsync(
                    "UPDATE alerts SET suppressed_count = suppressed_count + 1 WHERE alert_key = @AlertKey",
                    new { h.AlertKey },
                    tx);
                return (0, "cooldown");
            }

            await conn.ExecuteAsync(
                """
                INSERT INTO cooldowns (rule_id, entity_key, alert_key, notified_at) VALUES (@RuleId, @EntityKey, @AlertKey, @now)
                ON CONFLICT (rule_id, entity_key) DO UPDATE SET alert_key = EXCLUDED.alert_key, notified_at = EXCLUDED.notified_at
                """,
                new { c.RuleId, c.EntityKey, c.AlertKey, now },
                tx);
        }

        int queued;
        string decision;
        if (policy.Grouping && notify.GroupBy is not null && notify.GroupWait is { } wait && wait > TimeSpan.Zero)
        {
            var member = JsonSerializer.Serialize(
                new GroupMember(c.AlertKey, c.EntityKey, NotificationText.Title(c, ruleName), NotificationText.Detail(c), c.TriggerProducedAt, c.DetectedAt),
                NoiseCutJson.Options);
            var created = await conn.ExecuteScalarAsync<bool>(
                """
                INSERT INTO notification_groups (id, rule_id, group_key, kind, flush_at, members)
                VALUES (@Id, @RuleId, @GroupKey, @Kind, @FlushAt, jsonb_build_array(@Member::jsonb))
                ON CONFLICT (rule_id, group_key, kind) WHERE status = 'open'
                DO UPDATE SET members = notification_groups.members || EXCLUDED.members
                RETURNING (xmax = 0)
                """,
                new { Id = Guid.CreateVersion7(), c.RuleId, c.GroupKey, Kind = kind, FlushAt = now + wait, Member = member },
                tx);
            queued = 0;
            decision = created ? "notified" : "grouped"; // the group's first member "is" the notification; later ones merge into it
        }
        else
        {
            queued = 0;
            foreach (var channel in notify.Channels.Append("dashboard").Distinct(StringComparer.Ordinal))
            {
                var payload = NotificationPayload.From(c, ruleName, channel);
                await conn.ExecuteAsync(
                    """
                    INSERT INTO deliveries (id, notification_key, alert_key, kind, step, channel, payload)
                    VALUES (@Id, @NotificationKey, @AlertKey, @Kind, 0, @Channel, @Payload::jsonb)
                    """,
                    new { Id = Guid.CreateVersion7(), payload.NotificationKey, c.AlertKey, payload.Kind, Channel = channel, Payload = JsonSerializer.Serialize(payload, NoiseCutJson.Options) },
                    tx);
                queued++;
            }

            decision = "notified";
        }

        if (c.Status == CandidateStatus.Firing)
        {
            await conn.ExecuteAsync("UPDATE alerts SET notified = true WHERE alert_key = @AlertKey", new { c.AlertKey }, tx);
        }

        return (queued, decision);
    }
}

/// <summary>One alert inside a notification group.</summary>
public sealed record GroupMember(string AlertKey, string EntityKey, string Title, string Detail, DateTimeOffset TriggerProducedAt, DateTimeOffset DetectedAt);
