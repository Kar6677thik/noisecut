using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using NoiseCut.Core.Evaluation;
using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;

namespace NoiseCut.Core.Tests.TestSupport;

/// <summary>Tiny builders so each test reads like a timeline.</summary>
internal static class Ev
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset At(double minutes) => T0.AddMinutes(minutes);

    public static OrderEvent Order(string type, string orderId, DateTimeOffset at, string restaurant = "r-003", string zone = "z-01", JsonObject? data = null) => new()
    {
        EventId = Guid.CreateVersion7(at),
        Type = type,
        OccurredAt = at,
        OrderId = orderId,
        RestaurantId = restaurant,
        ZoneId = zone,
        Data = data ?? [],
    };

    public static OrderEvent Delayed(string orderId, DateTimeOffset at, int minutes = 20, string stage = "kitchen", string restaurant = "r-003") =>
        Order(EventTypes.Delayed, orderId, at, restaurant, data: new JsonObject { ["delayMinutes"] = minutes, ["stage"] = stage });

    public static OrderEvent Tick(DateTimeOffset at) => new() { EventId = Guid.CreateVersion7(at), Type = EventTypes.Tick, OccurredAt = at };

    public static Rule Rule(RuleDefinition def, int version = 1) => new(def, version);

    public static RuleEngine Engine(EmitMode mode = EmitMode.OnTransition, IBaselineSource? baselines = null, params RuleDefinition[] rules)
    {
        var engine = new RuleEngine(
            new EvaluationSettings { EmitMode = mode },
            baselines ?? NoBaseline.Instance,
            new FakeTimeProvider(T0),
            "test-engine");
        engine.SetRules(rules.Select(r => Rule(r)));
        return engine;
    }

    private static long _offset;

    /// <summary>Feed one event with time = occurredAt (so tests control time precisely).</summary>
    public static IReadOnlyList<AlertCandidate> Feed(this RuleEngine engine, OrderEvent evt) =>
        engine.Process(evt, evt.OccurredAt, new RecordInfo(evt.EventId, T0, 0, Interlocked.Increment(ref _offset)));

    public static List<AlertCandidate> FeedAll(this RuleEngine engine, IEnumerable<OrderEvent> events) =>
        events.SelectMany(engine.Feed).ToList();
}

internal sealed class FixedBaseline(double rate) : IBaselineSource
{
    public double? GetRate(string zoneId, DateTimeOffset hourStart) => rate;
}

/// <summary>The expected rate straight from the simulator's model: what a perfectly seeded week would contain.</summary>
internal sealed class ModelBaseline : IBaselineSource
{
    public static readonly ModelBaseline Instance = new();

    public double? GetRate(string zoneId, DateTimeOffset hourStart) => Simulation.DemandModel.BaseCancelRate(hourStart);
}
