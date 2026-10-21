using System.Text.Json;
using System.Text.Json.Nodes;
using Confluent.Kafka;
using Microsoft.Extensions.Options;
using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Serialization;
using NoiseCut.Core.Time;
using NoiseCut.Infrastructure.Kafka;
using NoiseCut.Infrastructure.Postgres;
using NoiseCut.Infrastructure.Telemetry;

namespace NoiseCut.Engine.Robust;

/// <summary>
/// The robust order stage: one <see cref="RuleEngine"/> per <c>order-events</c> partition (SPEC §6.1).
/// Restaurant and order rules decide here; zone rules only count, and publish <see cref="ZonePartial"/>s to <c>zone-partials</c>.
/// </summary>
public sealed class OrderStage(
    IOptions<EngineOptions> engineOptions,
    IOptions<KafkaOptions> kafkaOptions,
    RuleFeed rules,
    CheckpointStore checkpoints,
    BaselineCache baselines,
    TimeProvider clock,
    ILogger<OrderStage> logger)
    : CheckpointedStage<OrderStage.Processor>(engineOptions, kafkaOptions, rules, checkpoints, logger)
{
    protected override string Stage => "order";

    protected override string Topic => Topics.OrderEvents;

    protected override string GroupId => Options.GroupId;

    protected override Processor CreateProcessor(int partition) => new(
        new RuleEngine(Options.EvaluationSettings(), baselines, clock, Options.InstanceId, "order", EngineRole.Order, partition),
        Options.TimeMode == TimeMode.ProcessingTime ? new ProcessingTimeClock(clock, Options.TimeScale) : null);

    protected override void OnGauges(IEnumerable<Processor> processors)
    {
        // What this instance holds for zone rules: in robust mode a partial view by design; the zone stage holds the truth.
        foreach (var count in processors.SelectMany(p => p.Engine.ZoneHourCounts()).GroupBy(c => (c.ZoneId, c.HourStart))
                     .Select(g => new ZoneHourCount(g.Key.ZoneId, g.Key.HourStart, g.Sum(c => c.Numerator), g.Sum(c => c.Denominator)))
                     .GroupBy(c => c.ZoneId).Select(g => g.MaxBy(c => c.HourStart)!))
        {
            var zone = new KeyValuePair<string, object?>("zone", count.ZoneId);
            NoiseCutMetrics.EngineZoneHourOrders.Record(count.Denominator, zone, new("kind", "placed"));
            NoiseCutMetrics.EngineZoneHourOrders.Record(count.Numerator, zone, new("kind", "cancelled"));
        }
    }

    public sealed class Processor(RuleEngine engine, ProcessingTimeClock? processingClock) : IStageProcessor, IReportsRecordAge
    {
        private static readonly KeyValuePair<string, object?> StageTag = new("stage", "order");

        public RuleEngine Engine { get; } = engine;

        public DateTimeOffset Watermark => Engine.Watermark;

        public DateTimeOffset? LastProducedAt { get; private set; }

        public void SetRules(IReadOnlyList<Rule> rules) => Engine.SetRules(rules);

        public JsonObject SaveState() => Engine.SaveState();

        public void LoadState(JsonObject state) => Engine.LoadState(state);

        public void Handle(ConsumeResult<string?, byte[]> record, IProducer<string?, byte[]> producer, Action<DeliveryReport<string?, byte[]>> onDelivery)
        {
            var message = record.Message;
            LastProducedAt = message.Timestamp.UtcDateTime;

            if (!EventCodec.TryParse(message.Value, out var evt, out var error))
            {
                NoiseCutMetrics.DeadLettered.Add(1);
                producer.Produce(Topics.OrderEventsDlq, new Message<string?, byte[]>
                {
                    Key = message.Key,
                    Value = message.Value,
                    Headers = new Headers().WithHeader("x-error", error ?? "unknown").WithHeader("x-source", $"{record.Topic}/{record.Partition.Value}@{record.Offset.Value}"),
                }, onDelivery);
                return;
            }

            NoiseCutMetrics.EventsConsumed.Add(1, StageTag, new("type", evt!.Type));
            var time = processingClock?.Assign(evt.OccurredAt) ?? evt.OccurredAt;

            // SPEC §6.1 step 3: too late for window rules → park it for inspection, and count it.
            if (processingClock is null && !evt.IsTick)
            {
                switch (Engine.Classify(time))
                {
                    case Lateness.TooLate:
                        NoiseCutMetrics.LateEvents.Add(1, new KeyValuePair<string, object?>("outcome", "dropped"));
                        producer.Produce(Topics.LateEvents, new Message<string?, byte[]>
                        {
                            Key = message.Key,
                            Value = message.Value,
                            Headers = new Headers().WithHeader("x-watermark", Engine.Watermark.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
                        }, onDelivery);

                        // Windows are closed for it, but it can still disarm a stuck-pickup timer or resolve the alert.
                        foreach (var candidate in Engine.ProcessTooLate(evt, time, new RecordInfo(evt.EventId, message.Timestamp.UtcDateTime, record.Partition.Value, record.Offset.Value)))
                        {
                            Publish(producer, candidate, onDelivery);
                        }

                        return;
                    case Lateness.Late:
                        NoiseCutMetrics.LateEvents.Add(1, new KeyValuePair<string, object?>("outcome", "applied"));
                        break;
                }
            }

            var candidates = Engine.Process(evt, time, new RecordInfo(evt.EventId, message.Timestamp.UtcDateTime, record.Partition.Value, record.Offset.Value));
            foreach (var candidate in candidates)
            {
                Publish(producer, candidate, onDelivery);
            }

            foreach (var partial in Engine.LastPartials)
            {
                producer.Produce(Topics.ZonePartials, new Message<string?, byte[]>
                {
                    Key = partial.ZoneId, // one zone → one zone-partials partition → one zone-stage owner
                    Value = JsonSerializer.SerializeToUtf8Bytes(partial, NoiseCutJson.Options),
                }, onDelivery);
            }
        }

        internal static void Publish(IProducer<string?, byte[]> producer, AlertCandidate candidate, Action<DeliveryReport<string?, byte[]>> onDelivery)
        {
            var rule = new KeyValuePair<string, object?>("rule", candidate.RuleId);
            NoiseCutMetrics.Candidates.Add(1, rule, new("status", candidate.Status == CandidateStatus.Firing ? "firing" : "resolved"));
            NoiseCutMetrics.DetectionLatency.Record((candidate.DetectedAt - candidate.TriggerProducedAt).TotalSeconds, rule);
            producer.Produce(Topics.AlertCandidates, new Message<string?, byte[]>
            {
                Key = $"{candidate.RuleId}:{candidate.EntityKey}",
                Value = JsonSerializer.SerializeToUtf8Bytes(candidate, NoiseCutJson.Options),
            }, onDelivery);
        }
    }
}
