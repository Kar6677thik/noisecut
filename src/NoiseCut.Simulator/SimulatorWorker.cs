using Confluent.Kafka;
using Microsoft.Extensions.Options;
using NoiseCut.Core.Events;
using NoiseCut.Core.Simulation;
using NoiseCut.Infrastructure.Kafka;

namespace NoiseCut.Simulator;

/// <summary>
/// Drives the simulation in real time and writes its events to <c>order-events</c>:
/// order events keyed by restaurant (one order's events stay in order on one partition), plus a <c>clock.tick</c> to
/// <b>every</b> partition each second, so even a quiet partition shows that time is moving (ADR-0012).
/// </summary>
public sealed class SimulatorWorker(
    SimulationHost host,
    IOptions<SimulatorOptions> simOptions,
    IOptions<KafkaOptions> kafkaOptions,
    TimeProvider wall,
    ILogger<SimulatorWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = simOptions.Value;
        var partitions = await WaitForTopicAsync(stoppingToken);

        var resumeAfter = options.SimStart is null ? FindNewestEventTime(partitions) : null;
        var simStart = options.SimStart ?? Max(wall.GetUtcNow(), resumeAfter?.AddSeconds(1));
        var clock = new SimClock(simStart, wall.GetUtcNow(), options.TimeScale, wall);
        host.Start(new OrderSimulation(options.Seed, simStart, options.OrdersPerSimMinute), clock);
        logger.LogInformation(
            "Simulation started at sim time {SimStart:O} (timeScale={TimeScale}, peak={Rate} orders/sim-min, seed={Seed}, partitions={Partitions}{Resume})",
            simStart, options.TimeScale, options.OrdersPerSimMinute, options.Seed, partitions,
            resumeAfter is null ? "" : $", resumed after {resumeAfter:O}");

        using var producer = KafkaClients.CreateProducer(kafkaOptions.Value, "noisecut-simulator");
        var batch = new List<OrderEvent>(1024);
        var nextTick = wall.GetUtcNow();
        var nextLog = wall.GetUtcNow() + TimeSpan.FromSeconds(15);

        using var timer = new PeriodicTimer(options.StepInterval, wall);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                batch.Clear();
                host.Advance(batch);
                foreach (var evt in batch)
                {
                    Produce(producer, Topics.OrderEvents, evt.RestaurantId, null, evt);
                }

                Interlocked.Add(ref host.EventsProduced, batch.Count);

                var now = wall.GetUtcNow();
                if (now >= nextTick)
                {
                    // Ticks have no key: we choose the partition explicitly, because the point is to reach all of them.
                    var tick = new OrderEvent { EventId = Guid.CreateVersion7(), Type = EventTypes.Tick, OccurredAt = clock.Now };
                    for (var p = 0; p < partitions; p++)
                    {
                        Produce(producer, Topics.OrderEvents, null, p, tick);
                    }

                    Interlocked.Add(ref host.TicksProduced, partitions);
                    nextTick += options.TickInterval;
                    if (nextTick < now)
                    {
                        nextTick = now + options.TickInterval; // we fell behind (e.g. a GC pause); don't burst-catch-up
                    }
                }

                if (now >= nextLog)
                {
                    logger.LogInformation("sim={SimNow:O} events={Events} ticks={Ticks}", clock.Now, host.EventsProduced, host.TicksProduced);
                    nextLog = now + TimeSpan.FromSeconds(15);
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
        }
    }

    private void Produce(IProducer<string?, byte[]> producer, string topic, string? key, int? partition, OrderEvent evt)
    {
        var message = new Message<string?, byte[]> { Key = key, Value = EventCodec.Serialize(evt) };
        while (true)
        {
            try
            {
                if (partition is { } p)
                {
                    producer.Produce(new TopicPartition(topic, new Partition(p)), message, OnDelivery);
                }
                else
                {
                    producer.Produce(topic, message, OnDelivery);
                }

                return;
            }
            catch (ProduceException<string?, byte[]> ex) when (ex.Error.Code == ErrorCode.Local_QueueFull)
            {
                // Back-pressure: the local send queue is full. Let librdkafka drain a little, then try again.
                producer.Poll(TimeSpan.FromMilliseconds(50));
            }
        }
    }

    private void OnDelivery(DeliveryReport<string?, byte[]> report)
    {
        if (report.Error.IsError)
        {
            logger.LogError("Produce failed: {Reason}", report.Error.Reason);
        }
    }

    private async Task<int> WaitForTopicAsync(CancellationToken ct)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafkaOptions.Value.BootstrapServers }).Build();
        while (true)
        {
            try
            {
                var topic = admin.GetMetadata(Topics.OrderEvents, TimeSpan.FromSeconds(5)).Topics.SingleOrDefault();
                if (topic is { Error.IsError: false, Partitions.Count: > 0 })
                {
                    return topic.Partitions.Count;
                }
            }
            catch (KafkaException ex)
            {
                logger.LogWarning("Kafka not ready: {Reason}", ex.Error.Reason);
            }

            logger.LogInformation("Waiting for topic {Topic}…", Topics.OrderEvents);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>
    /// Read the last record of every partition and return the newest <c>occurredAt</c>.
    /// Resuming after it keeps event time moving forward across simulator restarts: the log is the source of truth.
    /// </summary>
    private DateTimeOffset? FindNewestEventTime(int partitions)
    {
        using var consumer = new ConsumerBuilder<string?, byte[]>(new ConsumerConfig
        {
            BootstrapServers = kafkaOptions.Value.BootstrapServers,
            GroupId = "noisecut-simulator-resume",
            EnableAutoCommit = false,
        }).Build();

        DateTimeOffset? newest = null;
        for (var p = 0; p < partitions; p++)
        {
            var tp = new TopicPartition(Topics.OrderEvents, new Partition(p));
            var offsets = consumer.QueryWatermarkOffsets(tp, TimeSpan.FromSeconds(5));
            if (offsets.High.Value <= offsets.Low.Value)
            {
                continue;
            }

            consumer.Assign(new TopicPartitionOffset(tp, new Offset(offsets.High.Value - 1)));
            var result = consumer.Consume(TimeSpan.FromSeconds(5));
            if (result?.Message is { } message && EventCodec.TryParse(message.Value, out var evt, out _))
            {
                newest = Max(evt!.OccurredAt, newest);
            }
        }

        return newest;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset? b) => b is { } v && v > a ? v : a;
}
