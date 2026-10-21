using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using NoiseCut.Core.Domain;
using NoiseCut.Core.Serialization;

namespace NoiseCut.Notifier;

/// <summary>
/// Grouping (SPEC §7.2): when an open group's <c>groupWait</c> has passed, turn all its members into <b>one</b> notification
/// per channel, e.g. "Restaurant delay burst: 6 restaurants in Koramangala". Groups are claimed with
/// <c>FOR UPDATE SKIP LOCKED</c>, exactly like the outbox, so several notifier replicas never flush the same group twice.
/// </summary>
public sealed class GroupFlusher(NpgsqlDataSource db, RuleCatalog rules, IOptions<NotifierOptions> options, ILogger<GroupFlusher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Grouping)
        {
            return;
        }

        await rules.Ready.Task.WaitAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FlushDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Group flush failed; retrying");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }

    public async Task<int> FlushDueAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var groups = (await conn.QueryAsync<(Guid Id, string RuleId, string GroupKey, string Kind, string Members)>(new CommandDefinition(
            """
            SELECT id, rule_id, group_key, kind, members::text FROM notification_groups
            WHERE status = 'open' AND flush_at <= now()
            ORDER BY flush_at LIMIT 20
            FOR UPDATE SKIP LOCKED
            """,
            transaction: tx,
            cancellationToken: ct))).ToList();

        foreach (var g in groups)
        {
            var members = JsonSerializer.Deserialize<List<GroupMember>>(g.Members, NoiseCutJson.Options) ?? [];
            var rule = rules.Find(g.RuleId);
            var payload = GroupPayload.Create(g.Id, g.RuleId, rule?.Definition.Name ?? g.RuleId, rule?.Definition.Severity.ToString().ToLowerInvariant() ?? "medium", g.GroupKey, g.Kind, members);
            foreach (var channel in (rule?.Definition.Notify.Channels ?? []).Append("dashboard").Distinct(StringComparer.Ordinal))
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO deliveries (id, notification_key, alert_key, kind, step, channel, payload)
                    VALUES (@Id, @Key, @AlertKey, @Kind, 0, @Channel, @Payload::jsonb)
                    """,
                    new
                    {
                        Id = Guid.CreateVersion7(),
                        Key = $"group:{g.Id}|{g.Kind}|0|{channel}",
                        AlertKey = $"group:{g.Id}",
                        g.Kind,
                        Channel = channel,
                        Payload = JsonSerializer.Serialize(payload with { NotificationKey = $"group:{g.Id}|{g.Kind}|0|{channel}" }, NoiseCutJson.Options),
                    },
                    tx);
            }

            await conn.ExecuteAsync("UPDATE notification_groups SET status = 'flushed', flushed_at = now() WHERE id = @Id", new { g.Id }, tx);
        }

        await tx.CommitAsync(ct);
        return groups.Count;
    }
}

/// <summary>The body of a grouped notification. Shaped like <see cref="NotificationPayload"/> where it can be, so every channel handles both.</summary>
public sealed record GroupPayload
{
    public required string NotificationKey { get; init; }

    public required string Kind { get; init; }

    public required string AlertKey { get; init; }

    public required string RuleId { get; init; }

    public required string Severity { get; init; }

    public required string EntityKey { get; init; }

    public required string GroupKey { get; init; }

    public required string Title { get; init; }

    public required string Detail { get; init; }

    public required int MemberCount { get; init; }

    public required IReadOnlyList<GroupMember> Members { get; init; }

    public required DateTimeOffset TriggerProducedAt { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }

    public static GroupPayload Create(Guid id, string ruleId, string ruleName, string severity, string groupKey, string kind, IReadOnlyList<GroupMember> members)
    {
        var distinct = members.GroupBy(m => m.AlertKey).Select(g => g.First()).ToList();
        var where = Topology.DisplayName(groupKey);
        var label = kind == "resolved" ? "RESOLVED" : severity.ToUpperInvariant();
        var noun = distinct.Count == 1 ? "alert" : "alerts";
        return new GroupPayload
        {
            NotificationKey = "",
            Kind = kind,
            AlertKey = $"group:{id}",
            RuleId = ruleId,
            Severity = severity,
            EntityKey = groupKey,
            GroupKey = groupKey,
            Title = string.Create(CultureInfo.InvariantCulture, $"[{label}] {ruleName} · {distinct.Count} {noun} in {where}"),
            Detail = string.Join("; ", distinct.Take(10).Select(m => $"{Topology.DisplayName(m.EntityKey)}: {m.Detail}"))
                     + (distinct.Count > 10 ? $"; +{distinct.Count - 10} more" : ""),
            MemberCount = distinct.Count,
            Members = distinct,
            TriggerProducedAt = distinct.Count == 0 ? DateTimeOffset.UtcNow : distinct.Min(m => m.TriggerProducedAt),
            DetectedAt = distinct.Count == 0 ? DateTimeOffset.UtcNow : distinct.Min(m => m.DetectedAt),
        };
    }
}
