using Dapper;
using Npgsql;

namespace NoiseCut.Api.Data;

public sealed record AlertRow(
    string AlertKey,
    string RuleId,
    int RuleVersion,
    string Severity,
    string EntityType,
    string EntityKey,
    string GroupKey,
    string Status,
    DateTimeOffset EpisodeStart,
    string Title,
    string Detail,
    string Observed,
    int CandidateCount,
    DateTimeOffset FirstDetectedAt,
    DateTimeOffset LastCandidateAt,
    DateTimeOffset? ResolvedAt);

public sealed record CandidateRow(string Status, Guid CandidateId, DateTimeOffset ReceivedAt, int RepeatCount, DateTimeOffset? LastRepeatAt);

public sealed record DeliveryRow(
    Guid Id,
    string Channel,
    string Kind,
    string Status,
    int Attempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    string? LastError,
    string NotificationKey);

public sealed record RuleRow(string Id, int Version, bool Enabled, string Definition, DateTimeOffset UpdatedAt, long Hits24h);

public sealed record ReceiptRow(string IdempotencyKey, DateTimeOffset FirstReceivedAt, DateTimeOffset LastReceivedAt, int ReceiveCount, string Body);

public sealed record Stats(
    long FiringNow,
    long Alerts24h,
    long CandidatesIn,
    long DistinctTransitions,
    long NotificationsSent,
    long NotificationsPending,
    long NotificationsDead,
    long SinkReceipts,
    long SinkDuplicates);

/// <summary>All dashboard reads. Plain SQL via Dapper (ADR-0011); every query is a single round trip.</summary>
public sealed class DashboardQueries(NpgsqlDataSource db)
{
    private const string AlertColumns =
        """
        alert_key AS AlertKey, rule_id AS RuleId, rule_version AS RuleVersion, severity AS Severity, entity_type AS EntityType,
        entity_key AS EntityKey, group_key AS GroupKey, status AS Status, episode_start AS EpisodeStart, title AS Title,
        detail AS Detail, observed::text AS Observed, candidate_count AS CandidateCount, first_detected_at AS FirstDetectedAt,
        last_candidate_at AS LastCandidateAt, resolved_at AS ResolvedAt
        """;

    public async Task<IReadOnlyList<AlertRow>> RecentAlertsAsync(int limit, string? status = null, string? ruleId = null)
    {
        await using var conn = await db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<AlertRow>(
            $"""
            SELECT {AlertColumns} FROM alerts
            WHERE (@status::text IS NULL OR status = @status) AND (@ruleId::text IS NULL OR rule_id = @ruleId)
            ORDER BY last_candidate_at DESC
            LIMIT @limit
            """,
            new { limit, status, ruleId });
        return rows.AsList();
    }

    public async Task<AlertRow?> AlertAsync(string alertKey)
    {
        await using var conn = await db.OpenConnectionAsync();
        return await conn.QuerySingleOrDefaultAsync<AlertRow>($"SELECT {AlertColumns} FROM alerts WHERE alert_key = @alertKey", new { alertKey });
    }

    public async Task<IReadOnlyList<CandidateRow>> CandidatesAsync(string alertKey)
    {
        await using var conn = await db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<CandidateRow>(
            """
            SELECT status AS Status, candidate_id AS CandidateId, received_at AS ReceivedAt, repeat_count AS RepeatCount, last_repeat_at AS LastRepeatAt
            FROM alert_candidates WHERE alert_key = @alertKey ORDER BY received_at
            """,
            new { alertKey });
        return rows.AsList();
    }

    public async Task<IReadOnlyList<DeliveryRow>> DeliveriesAsync(string alertKey)
    {
        await using var conn = await db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<DeliveryRow>(
            """
            SELECT id AS Id, channel AS Channel, kind AS Kind, status AS Status, attempts AS Attempts, created_at AS CreatedAt,
                   sent_at AS SentAt, last_error AS LastError, notification_key AS NotificationKey
            FROM deliveries WHERE alert_key = @alertKey ORDER BY created_at
            """,
            new { alertKey });
        return rows.AsList();
    }

    public async Task<IReadOnlyList<RuleRow>> RulesAsync()
    {
        await using var conn = await db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<RuleRow>(
            """
            SELECT r.id AS Id, r.version AS Version, r.enabled AS Enabled, r.definition::text AS Definition, r.updated_at AS UpdatedAt,
                   (SELECT count(*) FROM alerts a WHERE a.rule_id = r.id AND a.first_detected_at > now() - interval '24 hours') AS Hits24h
            FROM rules r ORDER BY r.id
            """);
        return rows.AsList();
    }

    public async Task<Stats> StatsAsync()
    {
        await using var conn = await db.OpenConnectionAsync();
        return await conn.QuerySingleAsync<Stats>(
            """
            SELECT
              (SELECT count(*) FROM alerts WHERE status = 'firing')                                        AS FiringNow,
              (SELECT count(*) FROM alerts WHERE first_detected_at > now() - interval '24 hours')         AS Alerts24h,
              (SELECT coalesce(sum(1 + repeat_count), 0) FROM alert_candidates)::bigint                   AS CandidatesIn,
              (SELECT count(*) FROM alert_candidates)                                                     AS DistinctTransitions,
              (SELECT count(*) FROM deliveries WHERE status = 'sent')                                     AS NotificationsSent,
              (SELECT count(*) FROM deliveries WHERE status = 'pending')                                  AS NotificationsPending,
              (SELECT count(*) FROM deliveries WHERE status = 'dead')                                     AS NotificationsDead,
              (SELECT coalesce(sum(receive_count), 0) FROM webhook_receipts)::bigint                      AS SinkReceipts,
              (SELECT coalesce(sum(receive_count - 1), 0) FROM webhook_receipts)::bigint                  AS SinkDuplicates
            """);
    }

    public async Task<IReadOnlyList<ReceiptRow>> ReceiptsAsync(int limit)
    {
        await using var conn = await db.OpenConnectionAsync();
        var rows = await conn.QueryAsync<ReceiptRow>(
            """
            SELECT idempotency_key AS IdempotencyKey, first_received_at AS FirstReceivedAt, last_received_at AS LastReceivedAt,
                   receive_count AS ReceiveCount, body::text AS Body
            FROM webhook_receipts ORDER BY last_received_at DESC LIMIT @limit
            """,
            new { limit });
        return rows.AsList();
    }

    /// <summary>Records one webhook arrival. Returns how many times this idempotency key has now been received.</summary>
    public async Task<int> RecordReceiptAsync(string idempotencyKey, string body)
    {
        await using var conn = await db.OpenConnectionAsync();
        return await conn.ExecuteScalarAsync<int>(
            """
            INSERT INTO webhook_receipts (idempotency_key, body) VALUES (@idempotencyKey, @body::jsonb)
            ON CONFLICT (idempotency_key) DO UPDATE
                SET receive_count = webhook_receipts.receive_count + 1, last_received_at = now()
            RETURNING receive_count
            """,
            new { idempotencyKey, body });
    }

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: ct)) == 1;
    }
}
