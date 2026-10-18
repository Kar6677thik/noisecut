using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Tests.TestSupport;
using static NoiseCut.Core.Tests.TestSupport.Ev;

namespace NoiseCut.Core.Tests;

/// <summary>Rule C: an order not picked up 20 minutes after food_ready.</summary>
public class AbsenceTests
{
    private static readonly RuleDefinition RuleC = ReferenceRules.StuckPickup;

    [Fact]
    public void Fires_when_the_watermark_passes_the_deadline()
    {
        var engine = Engine(rules: RuleC);

        Assert.Empty(engine.Feed(Order(EventTypes.FoodReady, "o-1", At(0))));
        Assert.Empty(engine.Feed(Tick(At(19.9))));

        var firing = Assert.Single(engine.Feed(Tick(At(20))));
        Assert.Equal(CandidateStatus.Firing, firing.Status);
        Assert.Equal("o-1", firing.EntityKey);
        Assert.Equal("r-003", firing.GroupKey);
        Assert.Equal(At(20), firing.EpisodeStart);
        Assert.Equal(20, firing.Observed["waitedMinutes"]);
    }

    [Fact]
    public void The_tick_that_advanced_time_is_the_trigger()
    {
        var engine = Engine(rules: RuleC);
        engine.Feed(Order(EventTypes.FoodReady, "o-1", At(0)));
        var tick = Tick(At(21));

        var firing = engine.Feed(tick).Single();

        Assert.Equal(tick.EventId, firing.TriggerEventId);
    }

    [Fact]
    public void A_timely_pickup_disarms_the_timer()
    {
        var engine = Engine(rules: RuleC);

        var output = engine.FeedAll(
        [
            Order(EventTypes.FoodReady, "o-1", At(0)),
            Order(EventTypes.PickedUp, "o-1", At(6)),
            Tick(At(60)),
        ]);

        Assert.Empty(output);
    }

    [Fact]
    public void A_cancellation_disarms_the_timer()
    {
        var engine = Engine(rules: RuleC);

        var output = engine.FeedAll(
        [
            Order(EventTypes.FoodReady, "o-1", At(0)),
            Order(EventTypes.Cancelled, "o-1", At(10)),
            Tick(At(60)),
        ]);

        Assert.Empty(output);
    }

    [Fact]
    public void A_pickup_after_the_alert_resolves_it_with_the_same_key()
    {
        var engine = Engine(rules: RuleC);

        var output = engine.FeedAll(
        [
            Order(EventTypes.FoodReady, "o-1", At(0)),
            Tick(At(21)),
            Order(EventTypes.PickedUp, "o-1", At(32)),
        ]);

        Assert.Equal([CandidateStatus.Firing, CandidateStatus.Resolved], output.Select(c => c.Status));
        Assert.Single(output.Select(c => c.AlertKey).Distinct());
        Assert.Equal(32, output[1].Observed["waitedMinutes"]);
    }

    [Fact]
    public void Fires_once_even_in_every_match_mode()
    {
        var engine = Engine(EmitMode.EveryMatch, rules: RuleC);

        var output = engine.FeedAll([Order(EventTypes.FoodReady, "o-1", At(0)), Tick(At(21)), Tick(At(22)), Tick(At(23))]);

        Assert.Single(output);
    }

    [Fact]
    public void Many_orders_fire_in_deadline_order()
    {
        var engine = Engine(rules: RuleC);

        var output = engine.FeedAll(
        [
            Order(EventTypes.FoodReady, "o-2", At(2)),
            Order(EventTypes.FoodReady, "o-1", At(1)),
            Tick(At(30)),
        ]);

        Assert.Equal(["o-1", "o-2"], output.Select(c => c.EntityKey));
    }

    [Fact]
    public void Naive_mode_forgets_a_pickup_that_arrives_before_food_ready()
    {
        // Out-of-order delivery: the pickup overtakes food_ready on the way to the engine.
        // The naive evaluator drops the early pickup, so the late food_ready arms a timer nobody will disarm.
        // This test pins down the bug that Break #2 measures and M3 fixes.
        var engine = Engine(rules: RuleC);

        var output = engine.FeedAll(
        [
            Order(EventTypes.PickedUp, "o-1", At(6)),
            Order(EventTypes.FoodReady, "o-1", At(5)),
            Tick(At(40)),
        ]);

        Assert.Equal(CandidateStatus.Firing, Assert.Single(output).Status);
    }
}
