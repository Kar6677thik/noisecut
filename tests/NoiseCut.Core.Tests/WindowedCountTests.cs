using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Tests.TestSupport;
using static NoiseCut.Core.Tests.TestSupport.Ev;

namespace NoiseCut.Core.Tests;

/// <summary>Rule A: 5+ distinct orders delayed &gt;15 min (kitchen) at one restaurant within a 10-minute sliding window.</summary>
public class WindowedCountTests
{
    private static readonly RuleDefinition RuleA = ReferenceRules.DelayBurst;

    [Fact]
    public void Four_delayed_orders_are_not_enough()
    {
        var engine = Engine(rules: RuleA);

        var output = engine.FeedAll(Enumerable.Range(1, 4).Select(i => Delayed($"o-{i}", At(i))));

        Assert.Empty(output);
    }

    [Fact]
    public void The_fifth_delayed_order_fires_immediately_without_waiting_for_the_window_to_close()
    {
        var engine = Engine(rules: RuleA);

        var output = engine.FeedAll(Enumerable.Range(1, 5).Select(i => Delayed($"o-{i}", At(i))));

        var firing = Assert.Single(output);
        Assert.Equal(CandidateStatus.Firing, firing.Status);
        Assert.Equal("r-003", firing.EntityKey);
        Assert.Equal("z-01", firing.GroupKey);
        Assert.Equal(5, firing.Observed["count"]);
        // The window ending at pane 12:05 starts at 11:56; that is the episode start.
        Assert.Equal(At(-4), firing.EpisodeStart);
        Assert.Equal($"restaurant-delay-burst:1:r-003:2026-10-17T11:56:00Z", firing.AlertKey);
    }

    [Fact]
    public void One_order_that_slips_repeatedly_counts_once()
    {
        var engine = Engine(rules: RuleA);

        var output = engine.FeedAll(
        [
            Delayed("o-1", At(1)), Delayed("o-1", At(2)), Delayed("o-1", At(3)),
            Delayed("o-2", At(4)), Delayed("o-3", At(5)), Delayed("o-4", At(6)),
        ]);

        Assert.Empty(output);
    }

    [Fact]
    public void Small_or_pickup_delays_do_not_count()
    {
        var engine = Engine(rules: RuleA);

        var output = engine.FeedAll(
        [
            .. Enumerable.Range(1, 5).Select(i => Delayed($"small-{i}", At(i), minutes: 15)),
            .. Enumerable.Range(1, 5).Select(i => Delayed($"pickup-{i}", At(i), minutes: 30, stage: "pickup")),
        ]);

        Assert.Empty(output);
    }

    [Fact]
    public void Delays_spread_wider_than_the_window_do_not_fire()
    {
        var engine = Engine(rules: RuleA);

        // One delayed order every 3 minutes: never more than 4 inside any 10-minute window.
        var output = engine.FeedAll(Enumerable.Range(0, 8).Select(i => Delayed($"o-{i}", At(i * 3))));

        Assert.Empty(output);
    }

    [Fact]
    public void Restaurants_are_counted_separately()
    {
        var engine = Engine(rules: RuleA);

        var output = engine.FeedAll(Enumerable.Range(1, 6).Select(i => Delayed($"o-{i}", At(i), restaurant: i % 2 == 0 ? "r-003" : "r-004")));

        Assert.Empty(output);
    }

    [Fact]
    public void On_transition_mode_stays_quiet_while_the_condition_holds()
    {
        var engine = Engine(EmitMode.OnTransition, rules: RuleA);

        var output = engine.FeedAll(Enumerable.Range(1, 9).Select(i => Delayed($"o-{i}", At(i * 0.5))));

        Assert.Single(output);
    }

    [Fact]
    public void Every_match_mode_repeats_the_same_alert_key_on_every_matching_event()
    {
        var engine = Engine(EmitMode.EveryMatch, rules: RuleA);

        var output = engine.FeedAll(Enumerable.Range(1, 9).Select(i => Delayed($"o-{i}", At(i * 0.5))));

        // Orders 5..9 each produce a firing candidate: the "500 alerts a minute" problem in miniature (Break #3).
        Assert.Equal(5, output.Count);
        Assert.Single(output.Select(c => c.AlertKey).Distinct());
    }

    [Fact]
    public void Resolves_only_after_the_watermark_closes_a_window_below_threshold()
    {
        var engine = Engine(rules: RuleA);
        var fired = engine.FeedAll(Enumerable.Range(1, 5).Select(i => Delayed($"o-{i}", At(i)))).Single();

        // The five orders sit in panes 12:01..12:05. Every window ending at 12:05..12:10 still contains all five.
        // The first window below the threshold ends at 12:11 (12:02..12:11, four orders). It is only known to be final
        // once the watermark passes its end, at 12:12.
        Assert.Empty(engine.Feed(Tick(At(11.5))));

        var resolved = Assert.Single(engine.Feed(Tick(At(12))));

        Assert.Equal(CandidateStatus.Resolved, resolved.Status);
        Assert.Equal(fired.AlertKey, resolved.AlertKey);
    }

    [Fact]
    public void A_new_burst_after_resolution_is_a_new_episode_with_a_new_key()
    {
        var engine = Engine(rules: RuleA);

        var first = engine.FeedAll(Enumerable.Range(1, 5).Select(i => Delayed($"a-{i}", At(i))));
        var gap = engine.Feed(Tick(At(30)));
        var second = engine.FeedAll(Enumerable.Range(1, 5).Select(i => Delayed($"b-{i}", At(40 + i))));

        Assert.Equal(CandidateStatus.Resolved, Assert.Single(gap).Status);
        Assert.NotEqual(Assert.Single(first).AlertKey, Assert.Single(second).AlertKey);
    }

    [Fact]
    public void Alert_keys_are_deterministic_across_engine_instances()
    {
        var events = Enumerable.Range(1, 5).Select(i => Delayed($"o-{i}", At(i))).ToList();

        var a = Engine(rules: RuleA).FeedAll(events).Single();
        var b = Engine(rules: RuleA).FeedAll(events).Single();

        Assert.Equal(a.AlertKey, b.AlertKey);
        Assert.NotEqual(a.CandidateId, b.CandidateId);
    }

    [Fact]
    public void Samples_reference_the_events_that_made_the_count()
    {
        var events = Enumerable.Range(1, 5).Select(i => Delayed($"o-{i}", At(i))).ToList();

        var firing = Engine(rules: RuleA).FeedAll(events).Single();

        Assert.Equal(events.Select(e => e.EventId).Order(), firing.SampleEventIds.Order());
        Assert.Equal(events[^1].EventId, firing.TriggerEventId);
    }
}
