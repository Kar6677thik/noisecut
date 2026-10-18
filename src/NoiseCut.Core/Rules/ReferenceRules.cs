using System.Text.Json;

namespace NoiseCut.Core.Rules;

/// <summary>
/// The three reference rules from SPEC §5.2–§5.4. The Migrator seeds them into PostgreSQL; tests use them directly.
/// </summary>
public static class ReferenceRules
{
    public static RuleDefinition DelayBurst { get; } = new()
    {
        Id = "restaurant-delay-burst",
        Name = "Restaurant delay burst",
        Description = "5+ orders delayed >15 min in the kitchen at one restaurant within 10 minutes",
        Kind = RuleKind.WindowedCount,
        Severity = Severity.High,
        Entity = "restaurantId",
        Source = new EventSelector
        {
            EventType = Events.EventTypes.Delayed,
            Where =
            [
                new FieldPredicate { Field = "data.delayMinutes", Op = CompareOp.Gt, Value = Json("15") },
                new FieldPredicate { Field = "data.stage", Op = CompareOp.Eq, Value = Json("\"kitchen\"") },
            ],
        },
        DistinctBy = "orderId",
        Window = new WindowSpec { Type = WindowType.Sliding, Size = TimeSpan.FromMinutes(10), Slide = TimeSpan.FromMinutes(1) },
        Condition = new ConditionSpec { Op = ConditionOp.Gte, Value = 5 },
        Notify = new NotifySpec
        {
            Channels = ["dashboard", "webhook"],
            Cooldown = TimeSpan.FromMinutes(15),
            GroupBy = "zoneId",
            GroupWait = TimeSpan.FromSeconds(30),
            Escalation = [new EscalationStep { After = TimeSpan.FromMinutes(10), Channels = ["email"] }],
        },
    };

    public static RuleDefinition CancellationSpike { get; } = new()
    {
        Id = "zone-cancellation-spike",
        Name = "Zone cancellation spike",
        Description = "A zone's cancellation rate this hour is at least 2x the same hour last week",
        Kind = RuleKind.RatioVsBaseline,
        Severity = Severity.Critical,
        Entity = "zoneId",
        Numerator = new EventSelector { EventType = Events.EventTypes.Cancelled },
        Denominator = new EventSelector { EventType = Events.EventTypes.Placed },
        Window = new WindowSpec { Type = WindowType.Tumbling, Size = TimeSpan.FromHours(1) },
        Baseline = new BaselineSpec { Offset = TimeSpan.FromDays(7) },
        Condition = new ConditionSpec { Op = ConditionOp.GteMultiple, Value = 2.0 },
        Guards = new GuardsSpec { MinDenominator = 300, MinBaselineRate = 0.02 },
        Notify = new NotifySpec
        {
            Channels = ["dashboard", "webhook"],
            Cooldown = TimeSpan.FromMinutes(30),
            Escalation = [new EscalationStep { After = TimeSpan.FromMinutes(5), Channels = ["email"] }],
        },
    };

    public static RuleDefinition StuckPickup { get; } = new()
    {
        Id = "stuck-pickup",
        Name = "Stuck pickup",
        Description = "An order is still not picked up 20 minutes after the food was ready",
        Kind = RuleKind.Absence,
        Severity = Severity.Medium,
        Entity = "orderId",
        Trigger = new EventSelector { EventType = Events.EventTypes.FoodReady },
        Expected = new EventSelector { EventType = Events.EventTypes.PickedUp },
        CancelIf = [Events.EventTypes.Cancelled],
        CorrelateBy = "orderId",
        Timeout = TimeSpan.FromMinutes(20),
        Notify = new NotifySpec
        {
            Channels = ["dashboard", "webhook"],
            GroupBy = "restaurantId",
            GroupWait = TimeSpan.FromMinutes(1),
        },
    };

    public static IReadOnlyList<RuleDefinition> All { get; } = [DelayBurst, CancellationSpike, StuckPickup];

    private static JsonElement Json(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
}
