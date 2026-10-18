using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;

namespace NoiseCut.Notifier;

/// <summary>
/// The outbox dispatcher (SPEC §7.3, ADR-0007). Loop:
/// <list type="number">
/// <item>Claim a batch of due rows with <c>FOR UPDATE SKIP LOCKED</c>. Rows another dispatcher holds are skipped instead of
///   waited on, so N notifier replicas share the work without double-sending.</item>
/// <item>Send each one. Success → <c>sent</c>. Failure → retry later with exponential backoff and jitter; after
///   <c>MaxAttempts</c> → <c>dead</c>.</item>
/// <item>Commit, which releases the locks (and delivers any dashboard NOTIFY).</item>
/// </list>
/// Trade-off, accepted for v1: the row lock is held while the HTTP call runs. A slow receiver therefore slows its batch,
/// but it can never cause a second dispatcher to send the same row concurrently.
/// </summary>
public sealed class DeliveryDispatcher(
    NpgsqlDataSource db,
    IEnumerable<IChannel> channels,
    IOptions<NotifierOptions> options,
    ILogger<DeliveryDispatcher> logger) : BackgroundService
{
    private readonly Dictionary<string, IChannel> _channels = channels.ToDictionary(c => c.Name, StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var handled = 0;
            try
            {
                handled = await DispatchBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Dispatch batch failed; will retry");
            }

            // A full batch means there is probably more waiting: go again immediately.
            if (handled < options.Value.Dispatcher.BatchSize)
            {
                await Task.Delay(options.Value.Dispatcher.PollInterval, stoppingToken);
            }
        }
    }

    public async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        var dispatcher = options.Value.Dispatcher;
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var rows = (await conn.QueryAsync<DeliveryRow>(
            new CommandDefinition(
                """
                SELECT id AS Id, notification_key AS NotificationKey, alert_key AS AlertKey, kind AS Kind, step AS Step,
                       channel AS Channel, payload::text AS Payload, attempts AS Attempts
                FROM deliveries
                WHERE status = 'pending' AND next_attempt_at <= now()
                ORDER BY next_attempt_at
                LIMIT @BatchSize
                FOR UPDATE SKIP LOCKED
                """,
                new { dispatcher.BatchSize },
                tx,
                cancellationToken: ct))).ToList();

        foreach (var row in rows)
        {
            string? error = null;
            if (_channels.TryGetValue(row.Channel, out var channel))
            {
                try
                {
                    await channel.SendAsync(row, conn, tx, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    error = ex.Message;
                }
            }
            else
            {
                error = $"channel '{row.Channel}' is not configured";
            }

            if (error is null)
            {
                await conn.ExecuteAsync(
                    "UPDATE deliveries SET status = 'sent', sent_at = now(), attempts = attempts + 1, last_error = NULL WHERE id = @Id",
                    new { row.Id },
                    tx);
                continue;
            }

            var attempts = row.Attempts + 1;
            var dead = attempts >= dispatcher.MaxAttempts;
            await conn.ExecuteAsync(
                """
                UPDATE deliveries
                SET attempts = @attempts, last_error = @error,
                    status = CASE WHEN @dead THEN 'dead' ELSE 'pending' END,
                    next_attempt_at = now() + make_interval(secs => @delaySeconds)
                WHERE id = @Id
                """,
                new { row.Id, attempts, error, dead, delaySeconds = Backoff(attempts, dispatcher.MaxBackoff).TotalSeconds },
                tx);

            if (dead)
            {
                logger.LogError("Delivery {Id} ({Channel}) is dead after {Attempts} attempts: {Error}", row.Id, row.Channel, attempts, error);
            }
            else
            {
                logger.LogWarning("Delivery {Id} ({Channel}) failed (attempt {Attempts}): {Error}", row.Id, row.Channel, attempts, error);
            }
        }

        await tx.CommitAsync(ct);
        return rows.Count;
    }

    /// <summary>
    /// 1 s, 2 s, 4 s, … capped at <paramref name="max"/>, each multiplied by a random factor in [0.5, 1.0).
    /// The jitter keeps many failed deliveries from retrying in lock-step and hammering a recovering receiver together.
    /// </summary>
    public static TimeSpan Backoff(int attempts, TimeSpan max)
    {
        var exponential = TimeSpan.FromSeconds(Math.Pow(2, Math.Max(0, attempts - 1)));
        var capped = exponential < max ? exponential : max;
        return capped * (0.5 + (Random.Shared.NextDouble() * 0.5));
    }
}
