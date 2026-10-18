using NoiseCut.Core.Events;
using NoiseCut.Core.Simulation;

namespace NoiseCut.Core.Tests;

public class SimulationTests
{
    // 12:00 IST = lunch peak.
    private static readonly DateTimeOffset LunchUtc = new(2026, 10, 17, 6, 30, 0, TimeSpan.Zero);

    private static List<OrderEvent> Run(OrderSimulation sim, TimeSpan duration, Action<OrderSimulation>? atStart = null)
    {
        atStart?.Invoke(sim);
        var events = new List<OrderEvent>();
        sim.AdvanceTo(sim.GeneratedUntil + duration, events);
        return events;
    }

    [Fact]
    public void The_same_seed_produces_the_same_events()
    {
        var a = Run(new OrderSimulation(42, LunchUtc, 300), TimeSpan.FromMinutes(30));
        var b = Run(new OrderSimulation(42, LunchUtc, 300), TimeSpan.FromMinutes(30));

        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a.Select(e => (e.EventId, e.Type, e.OccurredAt, e.OrderId)), b.Select(e => (e.EventId, e.Type, e.OccurredAt, e.OrderId)));
    }

    [Fact]
    public void A_different_seed_produces_different_events()
    {
        var a = Run(new OrderSimulation(1, LunchUtc, 300), TimeSpan.FromMinutes(5));
        var b = Run(new OrderSimulation(2, LunchUtc, 300), TimeSpan.FromMinutes(5));

        Assert.NotEqual(a.Select(e => e.EventId), b.Select(e => e.EventId));
    }

    [Fact]
    public void Events_come_out_in_event_time_order()
    {
        var events = Run(new OrderSimulation(7, LunchUtc, 300), TimeSpan.FromMinutes(60));

        Assert.True(events.Zip(events.Skip(1)).All(p => p.First.OccurredAt <= p.Second.OccurredAt));
    }

    [Fact]
    public void Peak_rate_is_roughly_the_configured_orders_per_minute()
    {
        var events = Run(new OrderSimulation(7, LunchUtc, 300), TimeSpan.FromMinutes(20));

        var placed = events.Count(e => e.Type == EventTypes.Placed);
        Assert.InRange(placed, 300 * 20 * 0.9, 300 * 20 * 1.1);
    }

    [Fact]
    public void Lifecycles_follow_the_spec_order()
    {
        var events = Run(new OrderSimulation(7, LunchUtc, 120), TimeSpan.FromHours(2));
        var rank = new Dictionary<string, int>
        {
            [EventTypes.Placed] = 0, [EventTypes.Accepted] = 1, [EventTypes.FoodReady] = 2, [EventTypes.PickedUp] = 3, [EventTypes.Delivered] = 4,
        };

        foreach (var order in events.GroupBy(e => e.OrderId))
        {
            var main = order.Where(e => rank.ContainsKey(e.Type)).Select(e => rank[e.Type]).ToList();
            Assert.Equal(main.Order(), main);
            Assert.Equal(0, main[0]);
            Assert.True(order.Count(e => e.Type == EventTypes.Cancelled) <= 1);
            Assert.False(order.Any(e => e.Type == EventTypes.Cancelled) && order.Any(e => e.Type == EventTypes.Delivered));
        }
    }

    [Fact]
    public void Baseline_cancellation_rate_is_about_three_percent()
    {
        var events = Run(new OrderSimulation(11, LunchUtc, 300), TimeSpan.FromHours(2));

        var placed = events.Count(e => e.Type == EventTypes.Placed);
        var cancelled = events.Count(e => e.Type == EventTypes.Cancelled);
        Assert.InRange((double)cancelled / placed, 0.02, 0.045);
    }

    [Fact]
    public void Kitchen_meltdown_delays_that_restaurant_by_more_than_15_minutes()
    {
        var events = Run(
            new OrderSimulation(3, LunchUtc, 300),
            TimeSpan.FromMinutes(30),
            sim => sim.Scenarios.Start(ScenarioBook.KitchenMeltdown, new ScenarioRequest { Target = "r-017" }, sim.GeneratedUntil));

        var bigDelays = events.Where(e => e.Type == EventTypes.Delayed && (int)e.Data!["delayMinutes"]! > 15).ToList();
        var perRestaurant = bigDelays.GroupBy(e => e.RestaurantId!).ToDictionary(g => g.Key, g => g.Count());
        Assert.True(perRestaurant["r-017"] >= 10);
        // Organic noise exists, but it is thin and spread out: no other restaurant comes close.
        Assert.True(perRestaurant.Where(p => p.Key != "r-017").Select(p => p.Value).DefaultIfEmpty(0).Max() < 4);
    }

    [Fact]
    public void Rider_shortage_makes_pickups_slower_than_twenty_minutes()
    {
        var sim = new OrderSimulation(5, LunchUtc, 300);
        sim.Scenarios.Start(ScenarioBook.RiderShortage, new ScenarioRequest { Target = "z-04" }, sim.GeneratedUntil);
        var events = Run(sim, TimeSpan.FromMinutes(60));

        var ready = events.Where(e => e.Type == EventTypes.FoodReady && e.ZoneId == "z-04" && e.OccurredAt < LunchUtc.AddMinutes(20))
            .ToDictionary(e => e.OrderId!, e => e.OccurredAt);
        var waits = events.Where(e => e.Type == EventTypes.PickedUp && ready.ContainsKey(e.OrderId!))
            .Select(e => (e.OccurredAt - ready[e.OrderId!]).TotalMinutes)
            .ToList();

        Assert.NotEmpty(waits);
        Assert.All(waits, w => Assert.InRange(w, 25, 40));
    }

    [Fact]
    public void Unknown_scenarios_and_targets_are_rejected()
    {
        var book = new ScenarioBook(1);

        Assert.Throws<ScenarioException>(() => book.Start("meteor-strike", new ScenarioRequest(), LunchUtc));
        Assert.Throws<ScenarioException>(() => book.Start(ScenarioBook.KitchenMeltdown, new ScenarioRequest { Target = "r-999" }, LunchUtc));
        Assert.Throws<ScenarioException>(() => book.Start(ScenarioBook.ZoneCancelSpike, new ScenarioRequest { Target = "z-99" }, LunchUtc));
    }

    [Fact]
    public void Alert_storm_melts_down_several_restaurants_in_one_zone()
    {
        var scenario = new ScenarioBook(1).Start(ScenarioBook.AlertStorm, new ScenarioRequest { Target = "z-01", Intensity = 6 }, LunchUtc);

        Assert.Equal(6, scenario.Restaurants.Count);
        Assert.All(scenario.Restaurants, r => Assert.Equal("z-01", Domain.Topology.FindRestaurant(r)!.ZoneId));
    }

    [Fact]
    public void Uuid7_embeds_the_timestamp_and_version()
    {
        var id = Uuid7.Create(LunchUtc, new Random(1));
        var text = id.ToString();

        Assert.Equal('7', text[14]);
        Assert.Equal(LunchUtc.ToUnixTimeMilliseconds(), Convert.ToInt64(text[..8] + text[9..13], 16));
    }

    [Fact]
    public void Seeded_baselines_cover_every_zone_and_hour_of_the_week()
    {
        var rows = BaselineSeeder.Generate(LunchUtc, days: 7, ordersPerSimMinute: 300, seed: 1);

        Assert.Equal(7 * 24 * 6, rows.Count);
        Assert.Equal(7 * 24, rows.Select(r => r.HourStart).Distinct().Count());
        var rate = (double)rows.Sum(r => r.Cancelled) / rows.Sum(r => r.Placed);
        Assert.InRange(rate, 0.025, 0.035);
    }
}
