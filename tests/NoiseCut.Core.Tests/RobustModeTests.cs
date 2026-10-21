using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Simulation;
using NoiseCut.Core.Tests.TestSupport;
using static NoiseCut.Core.Tests.TestSupport.Ev;

namespace NoiseCut.Core.Tests;

/// <summary>M3: checkpoint/restore determinism and event-time behaviour of the per-partition order stage.</summary>
public class RobustModeTests
{
    private static readonly DateTimeOffset LunchUtc = new(2026, 10, 17, 6, 30, 0, TimeSpan.Zero);
    private static readonly EvaluationSettings Robust = EvaluationSettings.EventTime(EmitMode.OnTransition);

    private static RuleEngine OrderEngine(int partition = 0, params RuleDefinition[] rules)
    {
        var engine = new RuleEngine(Robust, NoBaseline.Instance, new FakeTimeProvider(T0), "test", "order", EngineRole.Order, partition);
        engine.SetRules((rules.Length == 0 ? ReferenceRules.All : rules).Select(r => Rule(r)));
        return engine;
    }

    /// <summary>A lunch hour with a meltdown and a rider shortage, interleaved with a tick every 10 sim seconds.</summary>
    private static List<OrderEvent> Traffic(int seed, TimeSpan duration)
    {
        var sim = new OrderSimulation(seed, LunchUtc, 300);
        sim.Scenarios.Start(ScenarioBook.KitchenMeltdown, new ScenarioRequest { Target = "r-007" }, LunchUtc);
        sim.Scenarios.Start(ScenarioBook.RiderShortage, new ScenarioRequest { Target = "z-01" }, LunchUtc);
        var all = new List<OrderEvent>();
        var batch = new List<OrderEvent>();
        for (var t = LunchUtc; t < LunchUtc + duration; t += TimeSpan.FromSeconds(10))
        {
            batch.Clear();
            sim.AdvanceTo(t, batch);
            all.AddRange(batch.Where(e => e.ZoneId == "z-01")); // one "partition": zone z-01's restaurants
            all.Add(Tick(t));
        }

        return all;
    }

    private static List<(string Key, CandidateStatus Status)> Run(RuleEngine engine, IEnumerable<OrderEvent> events) =>
        events.SelectMany(e => engine.Process(e, e.OccurredAt, new RecordInfo(e.EventId, T0, 0, 0)))
              .Select(c => (c.AlertKey, c.Status))
              .ToList();

    [Fact]
    public void A_restored_engine_continues_exactly_like_one_that_never_stopped()
    {
        var events = Traffic(21, TimeSpan.FromMinutes(45));
        var reference = Run(OrderEngine(), events);
        Assert.Contains(reference, c => c.Key.StartsWith("restaurant-delay-burst", StringComparison.Ordinal));
        Assert.Contains(reference, c => c.Key.StartsWith("stuck-pickup", StringComparison.Ordinal));

        var split = events.Count / 2;
        var before = OrderEngine();
        var first = Run(before, events.Take(split));

        // Checkpoint = state serialized to text and parsed back: exactly what goes through PostgreSQL.
        var checkpoint = JsonNode.Parse(before.SaveState().ToJsonString())!.AsObject();
        var after = OrderEngine();
        after.LoadState(checkpoint);
        var second = Run(after, events.Skip(split));

        Assert.Equal(reference, [.. first, .. second]);
    }

    [Fact]
    public void Replaying_records_after_the_checkpoint_repeats_the_same_alert_keys()
    {
        // The crash scenario: checkpoint at k, keep going to k + m, crash, restore from k, replay k..k + m.
        var events = Traffic(22, TimeSpan.FromMinutes(45));
        var k = events.Count / 3;
        var m = events.Count / 3;

        var engine = OrderEngine();
        Run(engine, events.Take(k));
        var checkpoint = JsonNode.Parse(engine.SaveState().ToJsonString())!.AsObject();
        var beforeCrash = Run(engine, events.Skip(k).Take(m));

        var restarted = OrderEngine();
        restarted.LoadState(checkpoint);
        var replay = Run(restarted, events.Skip(k).Take(m));

        // Same transitions, same keys: the notifier's (alertKey, status) intake log absorbs every one of them.
        Assert.NotEmpty(beforeCrash);
        Assert.Equal(beforeCrash, replay);
    }

    [Fact]
    public void State_saved_under_an_older_rule_version_is_discarded()
    {
        var engine = OrderEngine(rules: ReferenceRules.DelayBurst);
        Run(engine, Enumerable.Range(1, 4).Select(i => Delayed($"o-{i}", At(i))));
        var checkpoint = engine.SaveState();

        var upgraded = new RuleEngine(Robust, NoBaseline.Instance, new FakeTimeProvider(T0), "test", "order", EngineRole.Order);
        upgraded.SetRules([Rule(ReferenceRules.DelayBurst, version: 2)]);

        Assert.Empty(upgraded.LoadState(checkpoint));
        Assert.Empty(Run(upgraded, [Delayed("o-5", At(5))]));
    }

    [Fact]
    public void An_early_pickup_is_remembered_so_the_late_food_ready_never_alerts()
    {
        var engine = OrderEngine(rules: ReferenceRules.StuckPickup);

        var output = Run(engine,
        [
            Order(EventTypes.PickedUp, "o-1", At(6)),
            Order(EventTypes.FoodReady, "o-1", At(5)),
            Tick(At(40)),
        ]);

        Assert.Empty(output);
    }

    [Fact]
    public void A_pickup_too_late_for_windows_still_disarms_or_resolves_the_stuck_pickup_timer()
    {
        var engine = OrderEngine(rules: ReferenceRules.StuckPickup);
        Run(engine, [Order(EventTypes.FoodReady, "o-1", At(0)), Order(EventTypes.FoodReady, "o-2", At(0)), Tick(At(10))]);
        var lateBeforeDeadline = Order(EventTypes.PickedUp, "o-1", At(3)); // 7 min behind the watermark: far beyond 2 min lateness

        Assert.Equal(Lateness.TooLate, engine.Classify(lateBeforeDeadline.OccurredAt));
        Assert.Empty(engine.ProcessTooLate(lateBeforeDeadline, lateBeforeDeadline.OccurredAt, new RecordInfo(null, T0, 0, 0)));

        var firing = Run(engine, [Tick(At(25))]);
        Assert.Equal("o-2", Assert.Single(firing).Key.Split(':')[2]); // o-1 was disarmed by its late pickup

        var latePickup = Order(EventTypes.PickedUp, "o-2", At(21));
        var resolved = engine.ProcessTooLate(latePickup, latePickup.OccurredAt, new RecordInfo(null, T0, 0, 0));
        Assert.Equal(CandidateStatus.Resolved, Assert.Single(resolved).Status);
    }

    [Fact]
    public void Lateness_is_measured_against_the_watermark()
    {
        var engine = OrderEngine();
        Run(engine, [Tick(At(10))]); // watermark = 10:00 − 30 s

        Assert.Equal(Lateness.OnTime, engine.Classify(At(9.6)));
        Assert.Equal(Lateness.Late, engine.Classify(At(8)));
        Assert.Equal(Lateness.TooLate, engine.Classify(At(7)));
    }

    [Fact]
    public void A_stalled_consumer_does_not_fire_timers_in_event_time()
    {
        // Break #2B in miniature: the whole backlog is processed "at once", but event time only moves as the data says.
        var engine = OrderEngine(rules: ReferenceRules.StuckPickup);
        var events = new List<OrderEvent>();
        for (var i = 0; i < 50; i++)
        {
            events.Add(Order(EventTypes.FoodReady, $"o-{i}", At(i * 0.4)));
            events.Add(Order(EventTypes.PickedUp, $"o-{i}", At((i * 0.4) + 6)));
        }

        var output = Run(engine, [.. events.OrderBy(e => e.OccurredAt), Tick(At(60))]);

        Assert.Empty(output);
    }

    [Fact]
    public void The_order_stage_publishes_partials_instead_of_deciding_zone_rules()
    {
        var engine = OrderEngine(partition: 4, rules: ReferenceRules.CancellationSpike);

        var candidates = Run(engine, [.. Enumerable.Range(0, 500).Select(i => Order(EventTypes.Cancelled, $"c-{i}", At(i * 0.001))), Tick(At(3))]);
        var partials = engine.LastPartials;

        Assert.Empty(candidates); // 100% cancelled, yet the order stage never alerts on its own
        Assert.Equal(6, partials.Count(p => p.HourStart == At(0))); // every zone reported, zeros included
        var z01 = partials.Single(p => p.ZoneId == "z-01");
        Assert.Equal((500L, 4), (z01.Numerator, z01.SourcePartition));
        Assert.All(partials, p => Assert.Equal(engine.Watermark, p.SourceWatermark));
    }
}
