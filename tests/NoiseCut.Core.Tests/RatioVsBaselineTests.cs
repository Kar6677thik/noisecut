using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Tests.TestSupport;
using static NoiseCut.Core.Tests.TestSupport.Ev;

namespace NoiseCut.Core.Tests;

/// <summary>Rule B: a zone's cancellation rate this hour is ≥ 2× the same hour last week (guards: 200 placed, 2% floor).</summary>
public class RatioVsBaselineTests
{
    private static readonly RuleDefinition RuleB = ReferenceRules.CancellationSpike;

    /// <summary>Placed and cancelled events for one zone, spread across the first <paramref name="minutes"/> minutes of the hour.</summary>
    private static IEnumerable<OrderEvent> Traffic(int placed, int cancelled, double minutes = 50, string zone = "z-02")
    {
        for (var i = 0; i < placed; i++)
        {
            yield return Order(EventTypes.Placed, $"p-{zone}-{i}", At(minutes * i / placed), zone: zone);
        }

        for (var i = 0; i < cancelled; i++)
        {
            yield return Order(EventTypes.Cancelled, $"c-{zone}-{i}", At(minutes * i / cancelled), zone: zone);
        }
    }

    private static IEnumerable<OrderEvent> Ordered(IEnumerable<OrderEvent> events) => events.OrderBy(e => e.OccurredAt);

    [Fact]
    public void Fires_when_the_rate_is_at_least_twice_the_baseline()
    {
        var engine = Engine(baselines: new FixedBaseline(0.03), rules: RuleB);

        var output = engine.FeedAll([.. Ordered(Traffic(placed: 400, cancelled: 40)), Tick(At(55))]);

        var firing = Assert.Single(output);
        Assert.Equal("z-02", firing.EntityKey);
        Assert.Equal(At(0), firing.EpisodeStart);
        Assert.Equal(0.03, firing.Observed["baselineRate"]);
        Assert.True(firing.Observed["rate"] >= 0.06);
    }

    [Fact]
    public void A_normal_rate_does_not_fire()
    {
        var engine = Engine(baselines: new FixedBaseline(0.03), rules: RuleB);

        var output = engine.FeedAll([.. Ordered(Traffic(placed: 400, cancelled: 14)), Tick(At(70))]);

        Assert.Empty(output);
    }

    [Fact]
    public void The_minimum_denominator_guard_ignores_tiny_samples()
    {
        // 50% cancelled, but only 10 orders: meaningless.
        var engine = Engine(baselines: new FixedBaseline(0.03), rules: RuleB);

        var output = engine.FeedAll([.. Ordered(Traffic(placed: 10, cancelled: 5)), Tick(At(55))]);

        Assert.Empty(output);
    }

    [Fact]
    public void A_missing_baseline_falls_back_to_the_minimum_rate_instead_of_dividing_by_zero()
    {
        var engine = Engine(baselines: NoBaseline.Instance, rules: RuleB);

        // 3% is below 2 × 2% = 4%: no alert, even though "anything > 0" is infinitely more than a zero baseline.
        Assert.Empty(engine.FeedAll([.. Ordered(Traffic(placed: 400, cancelled: 12)), Tick(At(55))]));

        var spike = Engine(baselines: NoBaseline.Instance, rules: RuleB).FeedAll([.. Ordered(Traffic(placed: 400, cancelled: 20)), Tick(At(55))]);
        Assert.Equal(0.02, Assert.Single(spike).Observed["baselineRate"]);
    }

    [Fact]
    public void One_episode_per_zone_hour_resolved_when_the_cohort_is_final()
    {
        var engine = Engine(baselines: new FixedBaseline(0.03), rules: RuleB);

        // Cohort hours stay open 45 minutes past hour end, for late cancellations of orders placed in that hour.
        var output = engine.FeedAll([.. Ordered(Traffic(placed: 400, cancelled: 40)), Tick(At(61)), Tick(At(104)), Tick(At(106))]);

        Assert.Equal([CandidateStatus.Firing, CandidateStatus.Resolved], output.Select(c => c.Status));
        Assert.Single(output.Select(c => c.AlertKey).Distinct());
    }

    [Fact]
    public void A_cancellation_counts_towards_the_hour_its_order_was_placed_in()
    {
        var engine = Engine(baselines: new FixedBaseline(0.03), rules: RuleB);

        // 400 orders placed in the 12:00 hour; 40 of them cancelled at 13:05–13:20, i.e. in the *next* hour.
        var placed = Enumerable.Range(0, 400).Select(i => Order(EventTypes.Placed, $"o-{i}", At(i * 0.14), zone: "z-02"));
        var cancelled = Enumerable.Range(0, 40).Select(i => Order(EventTypes.Cancelled, $"o-{i}", At(65 + (i * 0.4)), zone: "z-02"));
        var output = engine.FeedAll([.. placed, .. cancelled, Tick(At(70))]);

        var firing = Assert.Single(output);
        Assert.Equal(At(0), firing.EpisodeStart); // the 12:00 cohort, not the 13:00 hour the cancellations arrived in
        Assert.True(firing.Observed["rate"] >= 0.06); // fires as soon as the cohort's rate reaches 2 × 3%
    }

    [Fact]
    public void A_drop_in_demand_does_not_look_like_a_cancellation_spike()
    {
        // The flaw cohort attribution fixes: a busy hour (1,200 orders, 3% cancelled 25 min later), then demand drops to a quarter.
        // By arrival time, the quiet hour receives the busy hour's trailing cancellations over far fewer new orders.
        var events = new List<OrderEvent>();
        for (var i = 0; i < 1200; i++)
        {
            events.Add(Order(EventTypes.Placed, $"a-{i}", At(i * 0.05), zone: "z-02"));
            if (i % 33 == 0)
            {
                events.Add(Order(EventTypes.Cancelled, $"a-{i}", At((i * 0.05) + 25), zone: "z-02"));
            }
        }

        for (var i = 0; i < 300; i++)
        {
            events.Add(Order(EventTypes.Placed, $"b-{i}", At(60 + (i * 0.2)), zone: "z-02"));
            if (i % 33 == 0)
            {
                events.Add(Order(EventTypes.Cancelled, $"b-{i}", At(60 + (i * 0.2) + 25), zone: "z-02"));
            }
        }

        var ordered = events.OrderBy(e => e.OccurredAt).Append(Tick(At(130))).ToList();
        var cohort = Engine(baselines: new FixedBaseline(0.03), rules: RuleB).FeedAll(ordered);
        var byArrival = Engine(baselines: new FixedBaseline(0.03), rules: RuleB with { CohortBy = null }).FeedAll(ordered);

        Assert.DoesNotContain(cohort, c => c.Status == CandidateStatus.Firing);
        Assert.Contains(byArrival, c => c.Status == CandidateStatus.Firing); // the old definition raises a false spike
    }

    [Fact]
    public void Every_match_mode_repeats_at_every_pane_close()
    {
        var engine = Engine(EmitMode.EveryMatch, new FixedBaseline(0.03), RuleB);
        engine.FeedAll(Ordered(Traffic(placed: 400, cancelled: 40, minutes: 10)));

        var output = engine.FeedAll(Enumerable.Range(11, 5).Select(m => Tick(At(m))));

        Assert.Equal(5, output.Count(c => c.Status == CandidateStatus.Firing));
    }

    [Fact]
    public void Zones_are_independent()
    {
        var engine = Engine(baselines: new FixedBaseline(0.03), rules: RuleB);

        var output = engine.FeedAll(
        [
            .. Ordered([.. Traffic(placed: 400, cancelled: 40, zone: "z-02"), .. Traffic(placed: 400, cancelled: 8, zone: "z-03")]),
            Tick(At(55)),
        ]);

        Assert.Equal("z-02", Assert.Single(output).EntityKey);
    }
}
