using System.Text.Json;
using System.Text.Json.Nodes;

namespace NoiseCut.Core.Events;

/// <summary>Reads a rule field path such as <c>restaurantId</c> or <c>data.delayMinutes</c> from an event.</summary>
public static class EventFields
{
    public static string? GetString(OrderEvent evt, string path) => Get(evt, path) switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        var node => node.ToJsonString(),
    };

    public static JsonNode? Get(OrderEvent evt, string path) => path switch
    {
        "orderId" => evt.OrderId,
        "restaurantId" => evt.RestaurantId,
        "zoneId" => evt.ZoneId,
        "type" => evt.Type,
        _ when path.StartsWith("data.", StringComparison.Ordinal) => evt.Data?[path["data.".Length..]],
        _ => null,
    };

    /// <summary>True if the field exists on the event and the comparison holds. A missing field never matches.</summary>
    public static bool Matches(OrderEvent evt, string path, Rules.CompareOp op, JsonElement expected)
    {
        if (Get(evt, path) is not JsonValue actual)
        {
            return false;
        }

        if (expected.ValueKind == JsonValueKind.Number && TryGetNumber(actual, out var number))
        {
            return Compare(number.CompareTo(expected.GetDouble()), op);
        }

        if (expected.ValueKind == JsonValueKind.String && actual.TryGetValue<string>(out var text))
        {
            return Compare(string.CompareOrdinal(text, expected.GetString()), op);
        }

        return false;
    }

    private static bool TryGetNumber(JsonValue value, out double number)
    {
        if (value.TryGetValue(out number))
        {
            return true;
        }

        if (value.TryGetValue<int>(out var i))
        {
            number = i;
            return true;
        }

        if (value.TryGetValue<long>(out var l))
        {
            number = l;
            return true;
        }

        if (value.TryGetValue<JsonElement>(out var element) && element.ValueKind == JsonValueKind.Number)
        {
            number = element.GetDouble();
            return true;
        }

        return false;
    }

    private static bool Compare(int sign, Rules.CompareOp op) => op switch
    {
        Rules.CompareOp.Gt => sign > 0,
        Rules.CompareOp.Gte => sign >= 0,
        Rules.CompareOp.Lt => sign < 0,
        Rules.CompareOp.Lte => sign <= 0,
        Rules.CompareOp.Eq => sign == 0,
        Rules.CompareOp.Ne => sign != 0,
        _ => false,
    };
}
