using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using NoiseCut.Core.Serialization;

namespace NoiseCut.Notifier;

/// <summary>
/// Escalation (SPEC §7.2): an alert that is still firing, was notified, and nobody acknowledged after
/// <c>escalation[i].after</c> (wall clock) gets sent to that step's channels, e.g. email. Each step fires at most once per alert:
/// the <c>escalations</c> primary key makes the second attempt a no-op, even with several notifier replicas.
/// </summary>
public sealed class Escalator(NpgsqlDataSource db, RuleCatalog rules, IOptions<NotifierOptions> options, ILogger<Escalator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Escalation)
        {
            return;
        }

        await rules.Ready.Task.WaitAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EscalateDueAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Escalation pass failed; retrying");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    public async Task<int> EscalateDueAsync(CancellationToken ct)
    {
        var escalated = 0;
        await using var conn = await db.OpenConnectionAsync(ct);
        foreach (var rule in rules.All())
        {
            var steps = rule.Definition.Notify.Escalation;
            for (var i = 0; i < steps.Count; i++)
            {
                var step = i + 1;
                await using var tx = await conn.BeginTransactionAsync(ct);
                var due = (await conn.QueryAsync<(string AlertKey, string Title, string Detail, string EntityKey, string GroupKey, string Severity)>(new CommandDefinition(
                    """
                    SELECT a.alert_key, a.title, a.detail, a.entity_key, a.group_key, a.severity
                    FROM alerts a
                    WHERE a.rule_id = @RuleId AND a.status = 'firing' AND a.notified AND a.acked_at IS NULL
                      AND a.first_detected_at <= now() - make_interval(secs => @After)
                      AND NOT EXISTS (SELECT 1 FROM escalations e WHERE e.alert_key = a.alert_key AND e.step = @step)
                    LIMIT 50
                    """,
                    new { RuleId = rule.Id, After = steps[i].After.TotalSeconds, step },
                    tx,
                    cancellationToken: ct))).ToList();

                foreach (var a in due)
                {
                    var claimed = await conn.ExecuteAsync(
                        "INSERT INTO escalations (alert_key, step) VALUES (@AlertKey, @step) ON CONFLICT DO NOTHING",
                        new { a.AlertKey, step },
                        tx);
                    if (claimed == 0)
                    {
                        continue; // another replica got there first
                    }

                    foreach (var channel in steps[i].Channels)
                    {
                        var key = $"{a.AlertKey}|escalation|{step}|{channel}";
                        var payload = new JsonObject
                        {
                            ["notificationKey"] = key,
                            ["kind"] = "escalation",
                            ["alertKey"] = a.AlertKey,
                            ["ruleId"] = rule.Id,
                            ["severity"] = a.Severity,
                            ["entityKey"] = a.EntityKey,
                            ["groupKey"] = a.GroupKey,
                            ["title"] = $"[ESCALATED] {a.Title}",
                            ["detail"] = $"Still firing and not acknowledged after {steps[i].After.TotalMinutes:0} min. {a.Detail}",
                            ["step"] = step,
                            ["detectedAt"] = DateTimeOffset.UtcNow,
                        };
                        await conn.ExecuteAsync(
                            """
                            INSERT INTO deliveries (id, notification_key, alert_key, kind, step, channel, payload)
                            VALUES (@Id, @key, @AlertKey, 'escalation', @step, @channel, @Payload::jsonb)
                            """,
                            new { Id = Guid.CreateVersion7(), key, a.AlertKey, step, channel, Payload = payload.ToJsonString(NoiseCutJson.Options) },
                            tx);
                    }

                    escalated++;
                }

                await tx.CommitAsync(ct);
            }
        }

        if (escalated > 0)
        {
            logger.LogInformation("Escalated {Count} alert(s)", escalated);
        }

        return escalated;
    }
}
