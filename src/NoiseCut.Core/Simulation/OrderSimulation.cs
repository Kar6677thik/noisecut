using System.Text.Json.Nodes;
using NoiseCut.Core.Domain;
using NoiseCut.Core.Events;

namespace NoiseCut.Core.Simulation;

/// <summary>
/// A discrete-event simulation of order lifecycles (SPEC §9).
///
/// Every future event sits in a priority queue keyed by sim time. <see cref="AdvanceTo"/> moves the clock forward and
/// releases everything that is due, in time order. Each released event schedules the order's <i>next</i> step, and samples
/// its duration at that moment, so a scenario that starts mid-way affects orders already in flight.
///
/// Fully deterministic: the same seed, and the same sequence of AdvanceTo/StartScenario calls in sim time, produce the
/// same events with the same IDs.
/// </summary>
public sealed class OrderSimulation
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private static readonly string[] CancelReasons = ["customer", "restaurant", "other"];
    private static readonly TimeSpan SlowKitchenSpacing = TimeSpan.FromMinutes(4);

    private readonly Random _rng;
    private readonly PriorityQueue<Step, (DateTimeOffset At, long Seq)> _queue = new();
    private long _seq;
    private long _orderCounter;
    private readonly Dictionary<string, DateTimeOffset> _lastSlowDelay = new(StringComparer.Ordinal);

    public OrderSimulation(int seed, DateTimeOffset simStart, double ordersPerSimMinute)
    {
        _rng = new Random(seed);
        Scenarios = new ScenarioBook(seed + 1);
        GeneratedUntil = DateTimeOffset.FromUnixTimeSeconds(simStart.ToUnixTimeSeconds());
        OrdersPerSimMinute = ordersPerSimMinute;
    }

    public ScenarioBook Scenarios { get; }

    /// <summary>Peak arrival rate. The daily curve in <see cref="DemandModel"/> scales it down off-peak.</summary>
    public double OrdersPerSimMinute { get; set; }

    /// <summary>Sim time up to which arrivals have been generated and events released.</summary>
    public DateTimeOffset GeneratedUntil { get; private set; }

    public long OrdersCreated => _orderCounter;

    public int PendingSteps => _queue.Count;

    /// <summary>Generate arrivals and release every event due up to <paramref name="simNow"/> (whole seconds).</summary>
    public void AdvanceTo(DateTimeOffset simNow, ICollection<OrderEvent> output)
    {
        while (GeneratedUntil + Second <= simNow)
        {
            GenerateArrivals(GeneratedUntil);
            GeneratedUntil += Second;

            // Release only up to the generated horizon, so output stays in time order.
            while (_queue.TryPeek(out _, out var key) && key.At < GeneratedUntil)
            {
                var step = _queue.Dequeue();
                output.Add(Release(step, key.At));
            }
        }
    }

    private void GenerateArrivals(DateTimeOffset second)
    {
        var perSecond = OrdersPerSimMinute * DemandModel.DemandFactor(second) / 60.0;
        var count = Poisson(perSecond);
        for (var i = 0; i < count; i++)
        {
            var restaurant = Topology.Restaurants[_rng.Next(Topology.Restaurants.Count)];
            var order = new SimOrder($"o-{++_orderCounter:000000000}", restaurant);
            Schedule(second + TimeSpan.FromMilliseconds(_rng.Next(1000)), order, EventTypes.Placed, new JsonObject
            {
                ["itemCount"] = 1 + _rng.Next(6),
                ["valueInr"] = 150 + _rng.Next(1200),
            });
        }
    }

    private OrderEvent Release(Step step, DateTimeOffset at)
    {
        var order = step.Order;
        var evt = new OrderEvent
        {
            EventId = Uuid7.Create(at, _rng),
            Type = step.Type,
            OccurredAt = at,
            OrderId = order.Id,
            RestaurantId = order.Restaurant.Id,
            ZoneId = order.Restaurant.ZoneId,
            Data = step.Data ?? [],
        };

        switch (step.Type)
        {
            case EventTypes.Placed:
                OnPlaced(order, at);
                break;
            case EventTypes.Accepted:
                OnAccepted(order, at);
                break;
            case EventTypes.FoodReady:
                OnFoodReady(order, at);
                break;
            case EventTypes.PickedUp:
                Schedule(at + Minutes(15, 35), order, EventTypes.Delivered);
                break;
        }

        return evt;
    }

    private void OnPlaced(SimOrder order, DateTimeOffset at)
    {
        var cancelProbability = DemandModel.BaseCancelRate(at) * Scenarios.CancelMultiplier(order.Restaurant.ZoneId, at);
        if (_rng.NextDouble() < cancelProbability)
        {
            // Where in the lifecycle the cancellation happens: 40% before accept, 40% in the kitchen, 20% waiting for a rider.
            var roll = _rng.NextDouble();
            order.CancelStage = roll < 0.4 ? CancelStage.BeforeAccept : roll < 0.8 ? CancelStage.InKitchen : CancelStage.AwaitingPickup;
        }

        if (order.CancelStage == CancelStage.BeforeAccept)
        {
            ScheduleCancel(order, at + Minutes(1, 4), "customer");
            return;
        }

        Schedule(at + Minutes(0, 2), order, EventTypes.Accepted);
    }

    private void OnAccepted(SimOrder order, DateTimeOffset at)
    {
        var nominal = Minutes(10, 25);
        var extra = TimeSpan.Zero;
        var noticeAfter = Minutes(2, 5);
        if (Scenarios.IsKitchenMeltdown(order.Restaurant.Id, at))
        {
            extra = Minutes(20, 40);
        }
        else if (Scenarios.IsSlowKitchen(order.Restaurant.Id, at)
                 && (!_lastSlowDelay.TryGetValue(order.Restaurant.Id, out var last) || at - last >= SlowKitchenSpacing))
        {
            // A "near miss": badly late orders, but never closer than 4 minutes apart, so no 10-minute window can hold
            // the 5 that rule A needs. In event time this must never alert. Fixed notice time keeps the spacing intact.
            extra = Minutes(16, 25);
            noticeAfter = TimeSpan.FromMinutes(3);
            _lastSlowDelay[order.Restaurant.Id] = at;
        }
        else
        {
            // Organic noise: a few orders run a little late, very few run badly late.
            var roll = _rng.NextDouble();
            extra = roll < 0.002 ? Minutes(15, 20) : roll < 0.05 ? Minutes(3, 12) : TimeSpan.Zero;
        }

        if (order.CancelStage == CancelStage.InKitchen)
        {
            ScheduleCancel(order, at + Minutes(1, 9), "restaurant");
            return;
        }

        if (extra > TimeSpan.Zero)
        {
            // The kitchen notices it is running late a few minutes in, and the predicted ETA slips (SPEC §4.2).
            Schedule(at + noticeAfter, order, EventTypes.Delayed, new JsonObject
            {
                ["delayMinutes"] = (int)Math.Round(extra.TotalMinutes),
                ["stage"] = "kitchen",
            });
        }

        Schedule(at + nominal + extra, order, EventTypes.FoodReady);
    }

    private void OnFoodReady(SimOrder order, DateTimeOffset at)
    {
        var shortage = Scenarios.IsRiderShortage(order.Restaurant.ZoneId, at);
        var wait = shortage ? Minutes(25, 40) : Minutes(2, 8);

        if (order.CancelStage == CancelStage.AwaitingPickup)
        {
            ScheduleCancel(order, at + Minutes(1, 8), "no_rider");
            return;
        }

        if (shortage)
        {
            // Riders were promised within ~5 minutes; once 8 minutes pass, the pickup ETA visibly slips.
            Schedule(at + TimeSpan.FromMinutes(8), order, EventTypes.Delayed, new JsonObject
            {
                ["delayMinutes"] = (int)Math.Round(wait.TotalMinutes - 5),
                ["stage"] = "pickup",
            });
        }

        Schedule(at + wait, order, EventTypes.PickedUp, new JsonObject { ["riderId"] = $"rider-{_rng.Next(1, 400):000}" });
    }

    private void ScheduleCancel(SimOrder order, DateTimeOffset at, string defaultReason)
    {
        var reason = defaultReason == "customer" ? CancelReasons[_rng.Next(CancelReasons.Length)] : defaultReason;
        Schedule(at, order, EventTypes.Cancelled, new JsonObject { ["reason"] = reason });
    }

    private void Schedule(DateTimeOffset at, SimOrder order, string type, JsonObject? data = null) =>
        _queue.Enqueue(new Step(order, type, data), (at, _seq++));

    private TimeSpan Minutes(double min, double max) => TimeSpan.FromMinutes(min + (_rng.NextDouble() * (max - min)));

    // Knuth's algorithm: fine for the small means used here (a few arrivals per sim second).
    private int Poisson(double mean)
    {
        var limit = Math.Exp(-mean);
        var k = 0;
        var p = _rng.NextDouble();
        while (p > limit)
        {
            k++;
            p *= _rng.NextDouble();
        }

        return k;
    }

    private enum CancelStage { None, BeforeAccept, InKitchen, AwaitingPickup }

    private sealed class SimOrder(string id, Restaurant restaurant)
    {
        public string Id { get; } = id;

        public Restaurant Restaurant { get; } = restaurant;

        public CancelStage CancelStage { get; set; }
    }

    private sealed record Step(SimOrder Order, string Type, JsonObject? Data);
}
