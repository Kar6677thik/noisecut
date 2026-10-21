using System.Diagnostics;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using NoiseCut.Core.Rules;
using NoiseCut.Infrastructure.Kafka;
using NoiseCut.Infrastructure.Postgres;
using NoiseCut.Infrastructure.Telemetry;

namespace NoiseCut.Engine.Robust;

/// <summary>Per-partition logic plugged into <see cref="CheckpointedStage{TProcessor}"/>.</summary>
public interface IStageProcessor
{
    DateTimeOffset Watermark { get; }

    void SetRules(IReadOnlyList<Rule> rules);

    JsonObject SaveState();

    void LoadState(JsonObject state);

    void Handle(ConsumeResult<string?, byte[]> record, IProducer<string?, byte[]> producer, Action<DeliveryReport<string?, byte[]>> onDelivery);
}

/// <summary>
/// The robust consume loop (SPEC §6.3, ADR-0005), shared by the order stage and the zone stage.
///
/// <b>The invariant: never persist an offset whose consequences are not durable.</b> Every checkpoint therefore:
/// <list type="number">
/// <item>flushes the producer, so every candidate and partial caused by records before the offset has been acknowledged by Redpanda;</item>
/// <item>writes state + next offset for each owned partition in one PostgreSQL transaction, fenced by the partition's epoch;</item>
/// <item>only then commits the offset to Kafka, and only for lag visibility. PostgreSQL is the source of truth.</item>
/// </list>
/// A crash can make the engine redo work after the last checkpoint (at-least-once), but never skip work. The redone work
/// produces the same alert keys, which the notifier's intake log absorbs (ADR-0006).
///
/// Rebalances use the <b>cooperative-sticky</b> assignor: a partition keeps its owner whenever possible, and only the moving
/// partitions are paused. On assign: take ownership (epoch + 1), restore state, seek to the stored offset. On revoke:
/// checkpoint synchronously, then forget. On lost (session timed out, someone else may already own it): forget without writing.
/// </summary>
public abstract class CheckpointedStage<TProcessor>(
    IOptions<EngineOptions> engineOptions,
    IOptions<KafkaOptions> kafkaOptions,
    RuleFeed rules,
    CheckpointStore checkpoints,
    ILogger logger) : BackgroundService
    where TProcessor : class, IStageProcessor
{
    private readonly Dictionary<int, Owned> _owned = [];
    private volatile string? _produceFailure;
    private int _appliedRules;

    protected EngineOptions Options => engineOptions.Value;

    protected KafkaOptions Kafka => kafkaOptions.Value;

    protected abstract string Stage { get; }

    protected abstract string Topic { get; }

    protected abstract string GroupId { get; }

    protected abstract TProcessor CreateProcessor(int partition);

    /// <summary>Called on the consume thread before the first record, after rules are known.</summary>
    protected virtual void Initialize()
    {
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => RunAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken ct)
    {
        await rules.Ready.Task.WaitAsync(ct);
        Initialize();

        using var producer = KafkaClients.CreateProducer(Kafka, $"{Options.InstanceId}-{Stage}");
        using var consumer = new ConsumerBuilder<string?, byte[]>(new ConsumerConfig
        {
            BootstrapServers = Kafka.BootstrapServers,
            GroupId = GroupId,
            ClientId = $"{Options.InstanceId}-{Stage}",
            EnableAutoCommit = false,           // we decide when an offset counts as done
            EnableAutoOffsetStore = false,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,

            // Static membership (KIP-345): a restarted process with the same instance id gets its partitions straight back
            // instead of waiting for the dead member's session to time out, and the broker fences the old incarnation.
            // In a container the instance id (hostname + pid 1) survives a restart; with --scale each replica has its own hostname.
            GroupInstanceId = $"{Options.InstanceId}-{Stage}",
            SessionTimeoutMs = 10_000,
        })
            .SetPartitionsAssignedHandler((c, parts) => OnAssigned(parts))
            .SetPartitionsRevokedHandler((c, parts) => OnRevoked(c, producer, parts))
            .SetPartitionsLostHandler((c, parts) => OnLost(parts))
            .Build();

        consumer.Subscribe(Topic);
        logger.LogInformation(
            "{Stage} stage of {Instance} consuming {Topic} (group {Group}, time={Time}, emit={Emit}, checkpointed)",
            Stage, Options.InstanceId, Topic, GroupId, Options.TimeMode, Options.EmitMode);

        var sinceCheckpoint = Stopwatch.StartNew();
        var gauges = Stopwatch.StartNew();
        var recordsSinceCheckpoint = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ApplyRulesIfChanged();

                var result = consumer.Consume(TimeSpan.FromMilliseconds(250));
                if (result?.Message is not null && _owned.TryGetValue(result.Partition.Value, out var owned))
                {
                    owned.Processor.Handle(result, producer, OnDelivery);
                    owned.NextOffset = result.Offset.Value + 1;
                    recordsSinceCheckpoint++;
                }

                if (sinceCheckpoint.Elapsed >= Options.CheckpointInterval || recordsSinceCheckpoint >= Options.CheckpointEveryRecords)
                {
                    Checkpoint(consumer, producer, [.. _owned.Keys]);
                    sinceCheckpoint.Restart();
                    recordsSinceCheckpoint = 0;
                }

                if (gauges.Elapsed >= TimeSpan.FromSeconds(5))
                {
                    RecordGauges(consumer);
                    gauges.Restart();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            if (_produceFailure is null)
            {
                Checkpoint(consumer, producer, [.. _owned.Keys]);
            }

            consumer.Close();
        }
    }

    private void ApplyRulesIfChanged()
    {
        var snapshot = rules.Current;
        if (snapshot.Version == _appliedRules)
        {
            return;
        }

        _appliedRules = snapshot.Version;
        foreach (var owned in _owned.Values)
        {
            owned.Processor.SetRules(snapshot.Rules);
        }

        logger.LogInformation("{Stage} stage applied rule set v{Version}", Stage, snapshot.Version);
    }

    private IEnumerable<TopicPartitionOffset> OnAssigned(List<TopicPartition> partitions)
    {
        var result = new List<TopicPartitionOffset>();
        foreach (var tp in partitions)
        {
            var cp = checkpoints.AcquireAsync(Stage, Topic, tp.Partition.Value, Options.InstanceId, CancellationToken.None).GetAwaiter().GetResult();
            var processor = CreateProcessor(tp.Partition.Value);
            processor.SetRules(rules.Current.Rules);
            if (cp.State is not null)
            {
                processor.LoadState(cp.State);
            }

            _owned[tp.Partition.Value] = new Owned(processor, cp.Epoch) { NextOffset = cp.NextOffset };
            result.Add(new TopicPartitionOffset(tp, cp.NextOffset is { } offset ? new Offset(offset) : Offset.Beginning));
            logger.LogInformation(
                "{Stage}: took partition {Partition} (epoch {Epoch}), resuming at {Offset}{State}",
                Stage, tp.Partition.Value, cp.Epoch, cp.NextOffset?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "the beginning",
                cp.State is null ? " with empty state" : " with restored state");
        }

        _appliedRules = rules.Current.Version;
        return result;
    }

    private void OnRevoked(IConsumer<string?, byte[]> consumer, IProducer<string?, byte[]> producer, List<TopicPartitionOffset> partitions)
    {
        var ids = partitions.Select(p => p.Partition.Value).Where(_owned.ContainsKey).ToList();
        if (_produceFailure is null)
        {
            Checkpoint(consumer, producer, ids);
        }

        foreach (var id in ids)
        {
            _owned.Remove(id);
        }

        logger.LogInformation("{Stage}: released partitions {Partitions}", Stage, string.Join(",", ids));
    }

    private void OnLost(List<TopicPartitionOffset> partitions)
    {
        // Someone else may already own these. Writing a checkpoint now could only be fenced (or worse); just forget them.
        foreach (var p in partitions)
        {
            _owned.Remove(p.Partition.Value);
        }

        logger.LogWarning("{Stage}: lost partitions {Partitions} without checkpointing", Stage, string.Join(",", partitions.Select(p => p.Partition.Value)));
    }

    private void Checkpoint(IConsumer<string?, byte[]> consumer, IProducer<string?, byte[]> producer, IReadOnlyList<int> partitions)
    {
        ThrowIfProduceFailed();
        var writes = partitions
            .Select(p => (Id: p, Owned: _owned.GetValueOrDefault(p)))
            .Where(p => p.Owned?.NextOffset is not null)
            .ToList();
        if (writes.Count == 0)
        {
            return;
        }

        var stopwatch = Stopwatch.StartNew();

        // Step 1: everything we produced must be acknowledged before we claim the records that caused it are done.
        var stillQueued = producer.Flush(TimeSpan.FromSeconds(10));
        ThrowIfProduceFailed();
        if (stillQueued > 0)
        {
            NoiseCutMetrics.CheckpointsSkipped.Add(1, new KeyValuePair<string, object?>("stage", Stage));
            logger.LogWarning("{Stage}: {Count} records still unacknowledged; skipping this checkpoint", Stage, stillQueued);
            return;
        }

        // Step 2: state + offset, atomically, fenced.
        var fenced = checkpoints.SaveAsync(
            Stage,
            Topic,
            writes.Select(w => new CheckpointWrite(
                w.Id,
                w.Owned!.Epoch,
                w.Owned.NextOffset!.Value,
                w.Owned.Processor.Watermark == DateTimeOffset.MinValue ? null : w.Owned.Processor.Watermark,
                w.Owned.Processor.SaveState())).ToList(),
            CancellationToken.None).GetAwaiter().GetResult();

        foreach (var id in fenced)
        {
            // Another instance took this partition. Stop touching it; Kafka will catch up with the rebalance shortly.
            _owned.Remove(id);
            NoiseCutMetrics.CheckpointsFenced.Add(1, new KeyValuePair<string, object?>("stage", Stage));
            logger.LogWarning("{Stage}: checkpoint for partition {Partition} was fenced; dropping it", Stage, id);
        }

        // Step 3: tell Kafka too, so consumer-lag tools show the truth. Best effort only.
        try
        {
            consumer.Commit(writes.Where(w => !fenced.Contains(w.Id))
                .Select(w => new TopicPartitionOffset(Topic, new Partition(w.Id), new Offset(w.Owned!.NextOffset!.Value))));
        }
        catch (KafkaException ex)
        {
            logger.LogDebug("{Stage}: Kafka commit failed (PostgreSQL has the checkpoint): {Reason}", Stage, ex.Error.Reason);
        }

        NoiseCutMetrics.CheckpointDuration.Record(stopwatch.Elapsed.TotalSeconds, new KeyValuePair<string, object?>("stage", Stage));
    }

    private void OnDelivery(DeliveryReport<string?, byte[]> report)
    {
        if (report.Error.IsError)
        {
            // A lost candidate must not be "covered" by a later checkpoint. Stop: the restart replays from the last checkpoint.
            _produceFailure = $"{report.Topic}: {report.Error.Reason}";
            logger.LogCritical("{Stage}: produce failed ({Reason}); stopping so we restart from the last checkpoint", Stage, _produceFailure);
        }
    }

    private void ThrowIfProduceFailed()
    {
        if (_produceFailure is { } failure)
        {
            throw new InvalidOperationException($"{Stage} stage stopped after a failed produce: {failure}");
        }
    }

    private void RecordGauges(IConsumer<string?, byte[]> consumer)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var tp in consumer.Assignment)
        {
            var stage = new KeyValuePair<string, object?>("stage", Stage);
            var partition = new KeyValuePair<string, object?>("partition", tp.Partition.Value);
            var high = consumer.GetWatermarkOffsets(tp).High;
            var position = consumer.Position(tp);
            if (high != Offset.Unset && position != Offset.Unset)
            {
                NoiseCutMetrics.ConsumerLag.Record(Math.Max(0, high.Value - position.Value), stage, partition);
            }

            if (_owned.TryGetValue(tp.Partition.Value, out var owned))
            {
                if (owned.Processor.Watermark != DateTimeOffset.MinValue)
                {
                    NoiseCutMetrics.Watermark.Record(owned.Processor.Watermark.ToUnixTimeMilliseconds() / 1000.0, stage, partition);
                }

                if (owned.Processor is IReportsRecordAge aged && aged.LastProducedAt is { } produced)
                {
                    NoiseCutMetrics.RecordAge.Record(Math.Max(0, (now - produced).TotalSeconds), stage, partition);
                }
            }
        }

        OnGauges(_owned.Values.Select(o => o.Processor));
    }

    protected virtual void OnGauges(IEnumerable<TProcessor> processors)
    {
    }

    private sealed class Owned(TProcessor processor, long epoch)
    {
        public TProcessor Processor { get; } = processor;

        public long Epoch { get; } = epoch;

        /// <summary>The next offset to read; null until anything has been processed or restored.</summary>
        public long? NextOffset { get; set; }
    }
}

public interface IReportsRecordAge
{
    DateTimeOffset? LastProducedAt { get; }
}
