using System.Text;
using Confluent.Kafka;

namespace NoiseCut.Infrastructure.Kafka;

/// <summary>One place for client settings, so every producer gets the same durability guarantees.</summary>
public static class KafkaClients
{
    /// <summary>
    /// <c>acks=all</c>: the broker confirms only once the write is replicated (here: one node, but the habit matters).
    /// <c>enable.idempotence</c>: producer retries can't create duplicates or reorder records within a partition.
    /// </summary>
    public static ProducerConfig ProducerConfig(KafkaOptions options, string clientId) => new()
    {
        BootstrapServers = options.BootstrapServers,
        ClientId = clientId,
        EnableIdempotence = true,
        Acks = Acks.All,
        LingerMs = 5,
    };

    public static IProducer<string?, byte[]> CreateProducer(KafkaOptions options, string clientId) =>
        new ProducerBuilder<string?, byte[]>(ProducerConfig(options, clientId)).Build();

    public static Headers WithHeader(this Headers headers, string key, string value)
    {
        headers.Add(key, Encoding.UTF8.GetBytes(value));
        return headers;
    }

    public static string? GetString(this Headers? headers, string key) =>
        headers is not null && headers.TryGetLastBytes(key, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
}
