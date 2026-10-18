using System.Diagnostics;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Events;
using NoiseCut.Core.Serialization;
using NoiseCut.Core.Time;
using NoiseCut.Infrastructure.Kafka;

namespace NoiseCut.Engine;

/// <summary>
/// The order stage (SPEC §6.1), M1 edition: one consumer, state in memory, offsets auto-committed.
///
/// Read this as the "before" picture. Its weaknesses are deliberate and each one is a Break experiment in M2:
/// <list type="bullet">
/// <item>Auto-commit stores offsets on a timer, unrelated to whether this process's state survived (Break #1).</item>
/// <item>Window state lives only in memory; a restart forgets every open window (Break #1).</item>
/// <item>Records are timed by when we read them, not when they happened (Break #2).</item>
/// <item>EveryMatch re-emits for every matching event (Break #3).</item>
/// <item>State is per process, so with several engines each sees only part of a zone (Break #4).</item>
/// </list>
/// </summary>
public sealed class EngineWorker(
    IOptions<EngineOptions> engineOptions,
    IOptions<KafkaOptions> kafkaOptions,
    RuleFeed rules,
    Infrastructure.Postgres.BaselineCache baselines,
    TimeProvider clock,
    ILogger<EngineWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        // Consume() blocks a thread, so the loop gets a dedicated one instead of a thread-pool worker.
        Task.Factory.StartNew(() => RunAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken ct)
    {
        var options = engineOptions.Value;
        options.EnsureSupported();

        // Start only once rules are known; otherwise the first records would be evaluated against nothing.
        await rules.Ready.Task.WaitAsync(ct);

        var engine = new RuleEngine(
            new EvaluationSettings { EmitMode = options.EmitMode },
            baselines,
            clock,
            options.InstanceId);
        var processingClock = new ProcessingTimeClock(clock, options.TimeScale);

        using var producer = KafkaClients.CreateProducer(kafkaOptions.Value, options.InstanceId);
        using var consumer = new ConsumerBuilder<string?, byte[]>(new ConsumerConfig
        {
            BootstrapServers = kafkaOptions.Value.BootstrapServers,
            GroupId = options.GroupId,
            ClientId = options.InstanceId,
            // Naive: librdkafka stores each offset as soon as Consume() returns it and commits on a timer.
            EnableAutoCommit = true,
            AutoCommitIntervalMs = options.AutoCommitIntervalMs,
            AutoOffsetReset = Enum.Parse<AutoOffsetReset>(options.AutoOffsetReset, ignoreCase: true),
        })
            .SetPartitionsAssignedHandler((_, parts) => logger.LogInformation("Assigned partitions: {Partitions}", string.Join(",", parts.Select(p => p.Partition.Value))))
            .SetPartitionsRevokedHandler((_, parts) => logger.LogInformation("Revoked partitions: {Partitions}", string.Join(",", parts.Select(p => p.Partition.Value))))
            .Build();

        consumer.Subscribe(Topics.OrderEvents);
        logger.LogInformation(
            "Engine {Instance} consuming {Topic} (state={State}, time={Time}, emit={Emit})",
            options.InstanceId, Topics.OrderEvents, options.StateMode, options.TimeMode, options.EmitMode);

        var stats = new Stats();
        var statsTimer = Stopwatch.StartNew();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (rules.TakePending() is { } newRules && engine.SetRules(newRules))
                {
                    logger.LogInformation("Rule set applied: {Rules}", string.Join(", ", engine.ActiveRules.Select(r => $"{r.Id} v{r.Version}")));
                }

                var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (result?.Message is { } message)
                {
                    Handle(result, message, engine, processingClock, producer, stats);
                }

                if (statsTimer.Elapsed >= TimeSpan.FromSeconds(15))
                {
                    logger.LogInformation(
                        "consumed={Consumed} candidates={Candidates} dlq={Dlq} watermark={Watermark:O}",
                        stats.Consumed, stats.Candidates, stats.Dlq, engine.Watermark);
                    statsTimer.Restart();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            producer.Flush(TimeSpan.FromSeconds(5));
            consumer.Close();
        }
    }

    private void Handle(
        ConsumeResult<string?, byte[]> result,
        Message<string?, byte[]> message,
        RuleEngine engine,
        ProcessingTimeClock processingClock,
        IProducer<string?, byte[]> producer,
        Stats stats)
    {
        stats.Consumed++;

        if (!EventCodec.TryParse(message.Value, out var evt, out var error))
        {
            // Poison message: park it with the reason and keep going. A stuck partition is worse than a dropped record.
            stats.Dlq++;
            var headers = new Headers()
                .WithHeader("x-error", error ?? "unknown")
                .WithHeader("x-source", $"{result.Topic}/{result.Partition.Value}@{result.Offset.Value}");
            producer.Produce(Topics.OrderEventsDlq, new Message<string?, byte[]> { Key = message.Key, Value = message.Value, Headers = headers }, OnDelivery);
            return;
        }

        var time = processingClock.Assign(evt!.OccurredAt);
        var record = new RecordInfo(evt.EventId, message.Timestamp.UtcDateTime, result.Partition.Value, result.Offset.Value);

        foreach (var candidate in engine.Process(evt, time, record))
        {
            stats.Candidates++;
            producer.Produce(
                Topics.AlertCandidates,
                new Message<string?, byte[]>
                {
                    // Same rule + entity → same partition → the notifier sees one alert's transitions in order.
                    Key = $"{candidate.RuleId}:{candidate.EntityKey}",
                    Value = JsonSerializer.SerializeToUtf8Bytes(candidate, NoiseCutJson.Options),
                },
                OnDelivery);
            logger.LogDebug("{Status} {AlertKey}", candidate.Status, candidate.AlertKey);
        }
    }

    private void OnDelivery(DeliveryReport<string?, byte[]> report)
    {
        if (report.Error.IsError)
        {
            logger.LogError("Produce to {Topic} failed: {Reason}", report.Topic, report.Error.Reason);
        }
    }

    private sealed class Stats
    {
        public long Consumed { get; set; }

        public long Candidates { get; set; }

        public long Dlq { get; set; }
    }
}
