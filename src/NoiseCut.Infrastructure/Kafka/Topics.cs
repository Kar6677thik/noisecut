namespace NoiseCut.Infrastructure.Kafka;

/// <summary>Topic names from SPEC §4.4. Created by <c>deploy/compose</c> (rpk) or the k3s topic-init Job, never by services.</summary>
public static class Topics
{
    public const string OrderEvents = "order-events";
    public const string ZonePartials = "zone-partials";
    public const string AlertCandidates = "alert-candidates";
    public const string LateEvents = "late-events";
    public const string OrderEventsDlq = "order-events-dlq";
}

public sealed class KafkaOptions
{
    public const string Section = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:19092";
}
