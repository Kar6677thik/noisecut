using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Simulation;
using NoiseCut.Core.Tests.TestSupport;

namespace NoiseCut.Core.Tests;

/// <summary>
/// Simulator + rules together, with no Kafka in between. Each scenario must trigger its rule for its target,
/// and ordinary traffic must not trigger anything. This is the cheapest place to tune the simulator's noise model.
/// </summary>
public class ScenarioDetectionTests
{
    private static readonly DateTimeOffset LunchUtc = new(2026, 10, 17, 6, 30, 0, TimeSpan.Zero);

    private static List<AlertCandidate> Detect(int seed, TimeSpan duration, string? scenario = null, string? target = null, double ordersPerMinute = 300)
    {
        var sim = new OrderSimulation(seed, LunchUtc, ordersPerMinute);
        if (scenario is not null)
        {
            sim.Scenarios.Start(scenario, new ScenarioRequest { Target = target }, LunchUtc);
        }

        var engine = Ev.Engine(EmitMode.OnTransition, ModelBaseline.Instance, [.. ReferenceRules.All]);
        var candidates = new List<AlertCandidate>();
        var batch = new List<OrderEvent>();
        for (var t = LunchUtc; t < LunchUtc + duration; t += TimeSpan.FromSeconds(10))
        {
            batch.Clear();
            sim.AdvanceTo(t, batch);
            foreach (var evt in batch)
            {
                candidates.AddRange(engine.Feed(evt));
            }

            candidates.AddRange(engine.Feed(Ev.Tick(t)));
        }

        return candidates;
    }

    private static List<AlertCandidate> Firing(IEnumerable<AlertCandidate> candidates, string ruleId) =>
        candidates.Where(c => c.RuleId == ruleId && c.Status == CandidateStatus.Firing).ToList();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Ordinary_traffic_raises_no_alerts(int seed)
    {
        var candidates = Detect(seed, TimeSpan.FromHours(2));

        var firing = candidates.Where(c => c.Status == CandidateStatus.Firing).Select(c => $"{c.AlertKey} {string.Join(',', c.Observed)}");
        Assert.Equal([], firing);
    }

    [Fact]
    public void Kitchen_meltdown_is_detected_for_that_restaurant_only()
    {
        var firing = Firing(Detect(4, TimeSpan.FromMinutes(40), ScenarioBook.KitchenMeltdown, "r-017"), "restaurant-delay-burst");

        Assert.NotEmpty(firing);
        Assert.All(firing, c => Assert.Equal("r-017", c.EntityKey));
        Assert.True(firing[0].DetectedAt >= Ev.T0);
    }

    [Fact]
    public void Zone_cancel_spike_is_detected_for_that_zone_only()
    {
        var firing = Firing(Detect(5, TimeSpan.FromMinutes(90), ScenarioBook.ZoneCancelSpike, "z-02"), "zone-cancellation-spike");

        Assert.NotEmpty(firing);
        Assert.All(firing, c => Assert.Equal("z-02", c.EntityKey));
    }

    [Fact]
    public void Rider_shortage_raises_stuck_pickups_in_that_zone_only()
    {
        var all = Detect(6, TimeSpan.FromMinutes(70), ScenarioBook.RiderShortage, "z-04");
        var firing = Firing(all, "stuck-pickup");

        Assert.True(firing.Count >= 10);
        Assert.All(firing, c => Assert.Equal("z-04", Domain.Topology.FindRestaurant(c.GroupKey)!.ZoneId));
        // Every stuck order is eventually picked up, which resolves its alert.
        Assert.True(all.Count(c => c.RuleId == "stuck-pickup" && c.Status == CandidateStatus.Resolved) >= firing.Count * 0.8);
    }

    [Fact]
    public void Alert_storm_fires_for_many_restaurants_in_one_zone()
    {
        var firing = Firing(Detect(7, TimeSpan.FromMinutes(40), ScenarioBook.AlertStorm, "z-01"), "restaurant-delay-burst");

        Assert.True(firing.Select(c => c.EntityKey).Distinct().Count() >= 4);
        Assert.All(firing, c => Assert.Equal("z-01", c.GroupKey));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Slow_kitchen_is_a_near_miss_that_never_alerts_in_event_time(int seed)
    {
        var firing = Firing(Detect(seed, TimeSpan.FromMinutes(50), ScenarioBook.SlowKitchen, "r-005"), "restaurant-delay-burst");

        Assert.Empty(firing);
    }

    [Fact]
    public void Slow_kitchen_fires_falsely_when_a_backlog_is_squeezed_into_processing_time()
    {
        // Break #2 in miniature: the consumer was paused for 30 sim minutes, then reads the backlog in seconds.
        // In processing time every backlog record gets (almost) the same "now", so 4-minute-apart delays share one pane.
        var sim = new OrderSimulation(10, LunchUtc, 300);
        sim.Scenarios.Start(ScenarioBook.SlowKitchen, new ScenarioRequest { Target = "r-005" }, LunchUtc);
        var backlog = new List<OrderEvent>();
        sim.AdvanceTo(LunchUtc + TimeSpan.FromMinutes(35), backlog);

        var engine = Ev.Engine(EmitMode.OnTransition, NoBaseline.Instance, ReferenceRules.DelayBurst);
        var resumedAt = LunchUtc + TimeSpan.FromMinutes(35);
        var candidates = backlog.SelectMany(e => engine.Process(e, resumedAt, new RecordInfo(e.EventId, Ev.T0, 0, 0))).ToList();

        var delayedAtTarget = backlog.Count(e => e.Type == EventTypes.Delayed && e.RestaurantId == "r-005" && (int)e.Data!["delayMinutes"]! > 15);
        Assert.True(delayedAtTarget >= 5, $"expected at least 5 spaced-out delays, got {delayedAtTarget}");
        Assert.Contains(candidates, c => c.EntityKey == "r-005" && c.Status == CandidateStatus.Firing);
    }

    [Fact]
    public void Kitchen_meltdown_is_still_detected_off_peak()
    {
        // 03:00 IST, 20% of peak demand: the restaurant gets ~1 order a minute. Detection must still work.
        var sim = new OrderSimulation(8, new DateTimeOffset(2026, 10, 16, 21, 30, 0, TimeSpan.Zero), 300);
        var start = sim.GeneratedUntil;
        sim.Scenarios.Start(ScenarioBook.KitchenMeltdown, new ScenarioRequest { Target = "r-033" }, start);
        var engine = Ev.Engine(EmitMode.OnTransition, NoBaseline.Instance, ReferenceRules.DelayBurst);
        var batch = new List<OrderEvent>();
        var candidates = new List<AlertCandidate>();
        for (var t = start; t < start + TimeSpan.FromMinutes(40); t += TimeSpan.FromSeconds(10))
        {
            batch.Clear();
            sim.AdvanceTo(t, batch);
            candidates.AddRange(batch.SelectMany(engine.Feed));
            candidates.AddRange(engine.Feed(Ev.Tick(t)));
        }

        Assert.Contains(candidates, c => c.EntityKey == "r-033" && c.Status == CandidateStatus.Firing);
    }
}
