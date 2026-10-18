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
    public void One_episode_per_zone_hour_resolved_when_the_hour_closes()
    {
        var engine = Engine(baselines: new FixedBaseline(0.03), rules: RuleB);

        var output = engine.FeedAll([.. Ordered(Traffic(placed: 400, cancelled: 40)), Tick(At(59)), Tick(At(61))]);

        Assert.Equal([CandidateStatus.Firing, CandidateStatus.Resolved], output.Select(c => c.Status));
        Assert.Single(output.Select(c => c.AlertKey).Distinct());
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
