using System.Diagnostics;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using NoiseCut.Core.Events;
using NoiseCut.Core.Simulation;
using NoiseCut.Core.Time;
using NoiseCut.Infrastructure.Kafka;
using NoiseCut.Infrastructure.Telemetry;

namespace NoiseCut.Simulator;

/// <summary>
/// Drives the simulation in real time and writes its events to <c>order-events</c>:
/// order events keyed by restaurant (one order's events stay in order on one partition), plus a <c>clock.tick</c> to
/// <b>every</b> partition each second, so even a quiet partition shows that time is moving (ADR-0012).
///
/// Chaos (M2) sits between the simulation and the producer: it can hold an event back, send it twice, or hold everything for a
/// while. It never changes <c>occurredAt</c>.
/// </summary>
public sealed class SimulatorWorker(
    SimulationHost host,
    IOptions<SimulatorOptions> simOptions,
    IOptions<KafkaOptions> kafkaOptions,
    TimeProvider wall,
    ILogger<SimulatorWorker> logger) : BackgroundService
{
    private readonly PriorityQueue<OrderEvent, DateTimeOffset> _held = new();
    private readonly Dictionary<string, ZoneHour> _truth = new(StringComparer.Ordinal);
    private Random _chaosRng = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = simOptions.Value;
        _chaosRng = new Random(options.Seed + 2);
        var partitions = await WaitForTopicAsync(stoppingToken);

        var resumeAfter = options.SimStart is null ? FindNewestEventTime(partitions) : null;
        var simStart = options.SimStart ?? Max(PreferredStart(options), resumeAfter?.AddSeconds(1));
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
        var nextGauges = wall.GetUtcNow();

        using var timer = new PeriodicTimer(options.StepInterval, wall);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = wall.GetUtcNow();
                batch.Clear();
                host.Advance(batch);
                foreach (var evt in batch)
                {
                    CountTruth(evt);
                    Send(producer, evt, now);
                }

                ReleaseHeld(producer, now);

                if (now >= nextTick)
                {
                    // Ticks have no key: we choose the partition explicitly, because the point is to reach all of them.
                    // Chaos never delays ticks: time keeps moving even while events are stuck (that is what makes them "late").
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

                if (now >= nextGauges)
                {
                    RecordGauges();
                    nextGauges = now + TimeSpan.FromSeconds(5);
                }

                if (now >= nextLog)
                {
                    logger.LogInformation(
                        "sim={SimNow:O} events={Events} ticks={Ticks} held={Held} duplicated={Duplicated}",
                        clock.Now, host.EventsProduced, host.TicksProduced, _held.Count, host.EventsDuplicated);
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

    private void Send(IProducer<string?, byte[]> producer, OrderEvent evt, DateTimeOffset now)
    {
        var chaos = host.Chaos;

        if (host.BurstUntil > now)
        {
            // Late burst: everything waits until the burst ends, then goes out at once.
            _held.Enqueue(evt, host.BurstUntil);
            Interlocked.Increment(ref host.EventsHeld);
            NoiseCutMetrics.SimChaos.Add(1, new KeyValuePair<string, object?>("kind", "burst"));
            return;
        }

        if (chaos.OutOfOrderProbability > 0 && _chaosRng.NextDouble() < chaos.OutOfOrderProbability)
        {
            _held.Enqueue(evt, now + TimeSpan.FromSeconds(_chaosRng.NextDouble() * chaos.MaxHoldBackSeconds));
            Interlocked.Increment(ref host.EventsHeld);
            NoiseCutMetrics.SimChaos.Add(1, new KeyValuePair<string, object?>("kind", "held"));
            return;
        }

        Produce(producer, Topics.OrderEvents, evt.RestaurantId, null, evt);
        Interlocked.Increment(ref host.EventsProduced);

        if (chaos.DuplicateProbability > 0 && _chaosRng.NextDouble() < chaos.DuplicateProbability)
        {
            Produce(producer, Topics.OrderEvents, evt.RestaurantId, null, evt); // same eventId, same payload
            Interlocked.Increment(ref host.EventsDuplicated);
            NoiseCutMetrics.SimChaos.Add(1, new KeyValuePair<string, object?>("kind", "duplicated"));
        }
    }

    private void ReleaseHeld(IProducer<string?, byte[]> producer, DateTimeOffset now)
    {
        while (_held.TryPeek(out var evt, out var releaseAt) && releaseAt <= now)
        {
            _held.Dequeue();
            Produce(producer, Topics.OrderEvents, evt.RestaurantId, null, evt);
            Interlocked.Increment(ref host.EventsProduced);
        }
    }

    /// <summary>Ground truth for Break #4: what the simulator really generated this sim hour, per zone.</summary>
    private void CountTruth(OrderEvent evt)
    {
        NoiseCutMetrics.SimEvents.Add(1, new("type", evt.Type), new("zone", evt.ZoneId));
        if (evt.ZoneId is null || evt.Type is not (EventTypes.Placed or EventTypes.Cancelled))
        {
            return;
        }

        var hour = Panes.Floor(evt.OccurredAt, TimeSpan.FromHours(1));
        if (!_truth.TryGetValue(evt.ZoneId, out var current) || current.Hour != hour)
        {
            current = new ZoneHour(hour);
            _truth[evt.ZoneId] = current;
        }

        if (evt.Type == EventTypes.Placed)
        {
            current.Placed++;
        }
        else
        {
            current.Cancelled++;
        }
    }

    private void RecordGauges()
    {
        foreach (var (zone, counts) in _truth)
        {
            NoiseCutMetrics.SimZoneHourOrders.Record(counts.Placed, new("zone", zone), new("kind", "placed"));
            NoiseCutMetrics.SimZoneHourOrders.Record(counts.Cancelled, new("zone", zone), new("kind", "cancelled"));
        }

        foreach (var name in ScenarioBook.Names)
        {
            NoiseCutMetrics.SimActiveScenarios.Record(host.ActiveScenarios().Count(s => s.Name == name), new KeyValuePair<string, object?>("name", name));
        }

        NoiseCutMetrics.SimOrdersPerMinute.Record(host.OrdersPerSimMinute);
    }

    private DateTimeOffset PreferredStart(SimulatorOptions options)
    {
        var now = wall.GetUtcNow();
        if (options.StartAtLocalHour is not { } hour)
        {
            return now;
        }

        var localMidnight = new DateTimeOffset(now.ToOffset(DemandModel.LocalOffset).Date, DemandModel.LocalOffset);
        return localMidnight.AddHours(hour).ToUniversalTime();
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
        var stopwatch = Stopwatch.StartNew();
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

        logger.LogDebug("Scanned partition tails in {Elapsed}", stopwatch.Elapsed);
        return newest;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset? b) => b is { } v && v > a ? v : a;

    private sealed class ZoneHour(DateTimeOffset hour)
    {
        public DateTimeOffset Hour { get; } = hour;

        public long Placed { get; set; }

        public long Cancelled { get; set; }
    }
}
