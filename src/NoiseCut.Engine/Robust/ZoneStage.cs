using System.Text.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Serialization;
using NoiseCut.Infrastructure.Kafka;
using NoiseCut.Infrastructure.Postgres;
using NoiseCut.Infrastructure.Telemetry;

namespace NoiseCut.Engine.Robust;

/// <summary>
/// The zone stage (SPEC §6.4, ADR-0004): consumes <c>zone-partials</c>, merges every order-stage partition's counts, and
/// decides zone rules on the totals. Checkpointed exactly like the order stage, as stage <c>zone</c>.
/// When an hour becomes final, its totals are written to <c>zone_hourly_stats</c> as a <c>live</c> baseline, so after a week
/// of running, "same hour last week" compares against real history instead of the seeded synthetic week.
/// </summary>
public sealed class ZoneStage(
    IOptions<EngineOptions> engineOptions,
    IOptions<KafkaOptions> kafkaOptions,
    RuleFeed rules,
    CheckpointStore checkpoints,
    BaselineCache baselines,
    NpgsqlDataSource db,
    TimeProvider clock,
    ILogger<ZoneStage> logger)
    : CheckpointedStage<ZoneStage.Processor>(engineOptions, kafkaOptions, rules, checkpoints, logger)
{
    private int _sourcePartitions;

    protected override string Stage => "zone";

    protected override string Topic => Topics.ZonePartials;

    protected override string GroupId => Options.ZoneGroupId;

    protected override void Initialize()
    {
        // The zone watermark is the minimum over *all* order-events partitions, so we must know how many there are.
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = Kafka.BootstrapServers }).Build();
        _sourcePartitions = admin.GetMetadata(Topics.OrderEvents, TimeSpan.FromSeconds(10)).Topics.Single().Partitions.Count;
        logger.LogInformation("Zone stage merges partials from {Count} order-events partitions", _sourcePartitions);
    }

    protected override Processor CreateProcessor(int partition) =>
        new(new ZoneStageEngine(Options.EvaluationSettings(), baselines, clock, Options.InstanceId, _sourcePartitions), db, logger);

    protected override void OnGauges(IEnumerable<Processor> processors)
    {
        foreach (var count in processors.SelectMany(p => p.Engine.ZoneHourCounts()).GroupBy(c => c.ZoneId).Select(g => g.MaxBy(c => c.HourStart)!))
        {
            var zone = new KeyValuePair<string, object?>("zone", count.ZoneId);
            NoiseCutMetrics.EngineZoneHourOrders.Record(count.Denominator, zone, new("kind", "placed_merged"));
        }
    }

    public sealed class Processor(ZoneStageEngine engine, NpgsqlDataSource db, ILogger logger) : IStageProcessor, IReportsRecordAge
    {
        private static readonly KeyValuePair<string, object?> StageTag = new("stage", "zone");

        public ZoneStageEngine Engine { get; } = engine;

        public DateTimeOffset Watermark => Engine.Watermark;

        public DateTimeOffset? LastProducedAt { get; private set; }

        public void SetRules(IReadOnlyList<Rule> rules) => Engine.SetRules(rules);

        public JsonObject SaveState() => Engine.SaveState();

        public void LoadState(JsonObject state) => Engine.LoadState(state);

        public void Handle(ConsumeResult<string?, byte[]> record, IProducer<string?, byte[]> producer, Action<DeliveryReport<string?, byte[]>> onDelivery)
        {
            LastProducedAt = record.Message.Timestamp.UtcDateTime;
            ZonePartial? partial;
            try
            {
                partial = JsonSerializer.Deserialize<ZonePartial>(record.Message.Value, NoiseCutJson.Options);
            }
            catch (JsonException ex)
            {
                logger.LogError("Skipping unreadable partial at {Partition}@{Offset}: {Error}", record.Partition.Value, record.Offset.Value, ex.Message);
                return;
            }

            if (partial is null)
            {
                return;
            }

            NoiseCutMetrics.EventsConsumed.Add(1, StageTag, new("type", "zone.partial"));
            var candidates = Engine.Process(partial, new RecordInfo(null, partial.TriggerProducedAt, record.Partition.Value, record.Offset.Value));
            foreach (var candidate in candidates)
            {
                OrderStage.Processor.Publish(producer, candidate, onDelivery);
            }

            foreach (var hour in Engine.LastClosedHours)
            {
                RecordLiveBaseline(hour);
            }
        }

        // Idempotent upsert: a replay after a crash writes the same final totals again.
        private void RecordLiveBaseline(ClosedHour hour)
        {
            if (hour.RuleId != ReferenceRules.CancellationSpike.Id)
            {
                return; // zone_hourly_stats holds placed/cancelled, i.e. the cancellation rule's numerator and denominator
            }

            using var conn = db.OpenConnection();
            conn.Execute(
                """
                INSERT INTO zone_hourly_stats (zone_id, hour_start, placed, cancelled, source)
                VALUES (@ZoneId, @HourStart, @Denominator, @Numerator, 'live')
                ON CONFLICT (zone_id, hour_start) DO UPDATE
                    SET placed = EXCLUDED.placed, cancelled = EXCLUDED.cancelled, source = 'live'
                """,
                new { hour.ZoneId, hour.HourStart, Denominator = (int)hour.Denominator, Numerator = (int)hour.Numerator });
        }
    }
}
