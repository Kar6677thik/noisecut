namespace NoiseCut.Core.Events;

/// <summary>Event types from SPEC §4.2, and the payload fields each one carries.</summary>
public static class EventTypes
{
    public const string Placed = "order.placed";
    public const string Accepted = "order.accepted";
    public const string FoodReady = "order.food_ready";
    public const string PickedUp = "order.picked_up";
    public const string Delayed = "order.delayed";
    public const string Delivered = "order.delivered";
    public const string Cancelled = "order.cancelled";
    public const string Tick = "clock.tick";

    /// <summary>Known <c>data.*</c> fields per type. Rule validation uses this to reject typos like <c>data.delayMins</c>.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> DataFields { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [Placed] = new HashSet<string>(StringComparer.Ordinal) { "itemCount", "valueInr" },
            [Accepted] = new HashSet<string>(StringComparer.Ordinal),
            [FoodReady] = new HashSet<string>(StringComparer.Ordinal),
            [PickedUp] = new HashSet<string>(StringComparer.Ordinal) { "riderId" },
            [Delayed] = new HashSet<string>(StringComparer.Ordinal) { "delayMinutes", "stage" },
            [Delivered] = new HashSet<string>(StringComparer.Ordinal),
            [Cancelled] = new HashSet<string>(StringComparer.Ordinal) { "reason" },
            [Tick] = new HashSet<string>(StringComparer.Ordinal),
        };

    /// <summary>Envelope fields any rule may reference (besides <c>data.*</c>).</summary>
    public static IReadOnlySet<string> EnvelopeFields { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "orderId", "restaurantId", "zoneId", "type" };

    public static bool IsKnown(string type) => DataFields.ContainsKey(type);

    public static bool IsOrderEvent(string type) => IsKnown(type) && type != Tick;
}
