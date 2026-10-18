using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NoiseCut.Core.Events;

/// <summary>
/// The event envelope from SPEC §4.3. One record type for every event, including <c>clock.tick</c>.
/// <c>OccurredAt</c> is event time on the sim clock. The Kafka record timestamp (wall clock) is kept separately by the consumer.
/// </summary>
public sealed record OrderEvent
{
    public const int CurrentSchemaVersion = 1;

    public required Guid EventId { get; init; }

    public required string Type { get; init; }

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required DateTimeOffset OccurredAt { get; init; }

    public string? OrderId { get; init; }

    public string? RestaurantId { get; init; }

    public string? ZoneId { get; init; }

    public JsonObject? Data { get; init; }

    [JsonIgnore]
    public bool IsTick => Type == EventTypes.Tick;
}
