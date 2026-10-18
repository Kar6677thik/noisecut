using System.Text.Json;
using NoiseCut.Core.Serialization;

namespace NoiseCut.Core.Events;

/// <summary>
/// Turns bytes from Kafka into a validated <see cref="OrderEvent"/>, or a reason why not.
/// Anything that fails here is a "poison message": the consumer sends it to <c>order-events-dlq</c> and moves on (SPEC §4.3).
/// </summary>
public static class EventCodec
{
    public static byte[] Serialize(OrderEvent evt) => JsonSerializer.SerializeToUtf8Bytes(evt, NoiseCutJson.Options);

    public static bool TryParse(ReadOnlySpan<byte> utf8, out OrderEvent? evt, out string? error)
    {
        evt = null;
        try
        {
            evt = JsonSerializer.Deserialize<OrderEvent>(utf8, NoiseCutJson.Options);
        }
        catch (JsonException ex)
        {
            error = $"invalid json: {ex.Message}";
            return false;
        }

        error = evt is null ? "empty record" : Validate(evt);
        if (error is not null)
        {
            evt = null;
            return false;
        }

        return true;
    }

    private static string? Validate(OrderEvent evt)
    {
        if (evt.SchemaVersion != OrderEvent.CurrentSchemaVersion)
        {
            return $"unsupported schemaVersion {evt.SchemaVersion}";
        }

        if (!EventTypes.IsKnown(evt.Type))
        {
            return $"unknown event type '{evt.Type}'";
        }

        if (evt.EventId == Guid.Empty)
        {
            return "missing eventId";
        }

        if (evt.OccurredAt == default)
        {
            return "missing occurredAt";
        }

        if (evt.IsTick)
        {
            return null;
        }

        if (string.IsNullOrEmpty(evt.OrderId) || string.IsNullOrEmpty(evt.RestaurantId) || string.IsNullOrEmpty(evt.ZoneId))
        {
            return "order events need orderId, restaurantId and zoneId";
        }

        return null;
    }
}
