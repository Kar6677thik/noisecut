using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Serialization;
using NoiseCut.Infrastructure.Kafka;

namespace NoiseCut.Notifier;

/// <summary>
/// Consumes <c>alert-candidates</c> with manual commits: process → DB commit → Kafka commit.
/// That order makes intake at-least-once; the intake log makes it idempotent. Together: effectively once (ADR-0006).
/// </summary>
public sealed class IntakeWorker(
    CandidateIntake intake,
    RuleCatalog rules,
    IOptions<NotifierOptions> notifierOptions,
    IOptions<KafkaOptions> kafkaOptions,
    ILogger<IntakeWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => RunAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken ct)
    {
        var options = notifierOptions.Value;
        options.EnsureSupported();
        await rules.Ready.Task.WaitAsync(ct);

        using var consumer = new ConsumerBuilder<string?, byte[]>(new ConsumerConfig
        {
            BootstrapServers = kafkaOptions.Value.BootstrapServers,
            GroupId = options.GroupId,
            ClientId = options.InstanceId,
            EnableAutoCommit = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(Topics.AlertCandidates);
        logger.LogInformation("Notifier intake consuming {Topic} (dedup={Dedup})", Topics.AlertCandidates, options.Dedup);

        long received = 0, repeats = 0, queued = 0;
        var lastLog = DateTimeOffset.UtcNow;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (result?.Message is { } message)
                {
                    AlertCandidate? candidate = null;
                    try
                    {
                        candidate = JsonSerializer.Deserialize<AlertCandidate>(message.Value, NoiseCutJson.Options);
                    }
                    catch (JsonException ex)
                    {
                        logger.LogError("Skipping unreadable candidate at {Partition}@{Offset}: {Error}", result.Partition.Value, result.Offset.Value, ex.Message);
                    }

                    if (candidate is not null)
                    {
                        var outcome = await HandleWithRetryAsync(candidate, ct);
                        received++;
                        repeats += outcome.FirstTime ? 0 : 1;
                        queued += outcome.DeliveriesQueued;
                    }

                    // Only now is it safe to tell Kafka we are done with this record.
                    consumer.Commit(result);
                }

                if (DateTimeOffset.UtcNow - lastLog > TimeSpan.FromSeconds(15))
                {
                    logger.LogInformation("candidates={Received} repeats={Repeats} deliveriesQueued={Queued}", received, repeats, queued);
                    lastLog = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            consumer.Close();
        }
    }

    // A database blip must not skip a candidate: keep retrying the same record until it succeeds or we shut down.
    private async Task<CandidateIntake.Outcome> HandleWithRetryAsync(AlertCandidate candidate, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await intake.HandleAsync(candidate, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt - 1)));
                logger.LogWarning(ex, "Intake failed for {AlertKey} (attempt {Attempt}); retrying in {Delay}", candidate.AlertKey, attempt, delay);
                await Task.Delay(delay, ct);
            }
        }
    }
}
