using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using NoiseCut.Infrastructure.Webhooks;

namespace NoiseCut.Notifier;

/// <summary>A row claimed from the outbox.</summary>
public sealed record DeliveryRow(Guid Id, string NotificationKey, string AlertKey, string Kind, int Step, string Channel, string Payload, int Attempts);

/// <summary>One way of telling a human. Throwing means "failed, retry later".</summary>
public interface IChannel
{
    string Name { get; }

    /// <param name="conn">The dispatcher's connection, inside the transaction that holds the row lock.</param>
    Task SendAsync(DeliveryRow delivery, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct);
}

/// <summary>
/// The dashboard channel is a PostgreSQL <c>NOTIFY</c>. The Api <c>LISTEN</c>s and pushes it to browsers over SSE.
/// NOTIFY is transactional: it is delivered only if the dispatcher's transaction (which marks the row sent) commits.
/// </summary>
public sealed class DashboardChannel : IChannel
{
    public const string NotifyChannel = "alerts_feed";

    public string Name => "dashboard";

    public Task SendAsync(DeliveryRow delivery, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        // NOTIFY payloads are limited to 8000 bytes, so send a compact summary rather than the full payload.
        var full = JsonNode.Parse(delivery.Payload)!.AsObject();
        var summary = new JsonObject
        {
            ["alertKey"] = delivery.AlertKey,
            ["kind"] = delivery.Kind,
            ["ruleId"] = full["ruleId"]?.DeepClone(),
            ["severity"] = full["severity"]?.DeepClone(),
            ["entityKey"] = full["entityKey"]?.DeepClone(),
            ["groupKey"] = full["groupKey"]?.DeepClone(),
            ["title"] = full["title"]?.DeepClone(),
            ["detail"] = full["detail"]?.DeepClone(),
            ["detectedAt"] = full["detectedAt"]?.DeepClone(),
        };
        return conn.ExecuteAsync(new CommandDefinition(
            "SELECT pg_notify(@channel, @payload)",
            new { channel = NotifyChannel, payload = summary.ToJsonString() },
            tx,
            cancellationToken: ct));
    }
}

/// <summary>
/// HTTP POST with an HMAC signature and an Idempotency-Key (SPEC §7.4). Delivery is at-least-once: if we crash after the
/// receiver answered but before we mark the row sent, it is sent again with the same key, and the receiver can tell.
/// </summary>
public sealed class WebhookChannel(IHttpClientFactory httpClients, IOptions<NotifierOptions> options) : IChannel
{
    public const string HttpClientName = "webhook";

    public string Name => "webhook";

    public async Task SendAsync(DeliveryRow delivery, NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        var webhook = options.Value.Webhook;
        var body = webhook.Format.Equals("slack", StringComparison.OrdinalIgnoreCase) ? SlackBody(delivery.Payload) : delivery.Payload;
        var bytes = Encoding.UTF8.GetBytes(body);

        using var request = new HttpRequestMessage(HttpMethod.Post, webhook.Url) { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        request.Headers.Add(WebhookSignature.IdempotencyHeader, delivery.NotificationKey);
        if (!string.IsNullOrEmpty(webhook.Secret))
        {
            request.Headers.Add(WebhookSignature.Header, WebhookSignature.Compute(webhook.Secret, bytes));
        }

        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"webhook answered {(int)response.StatusCode} {response.ReasonPhrase}");
        }
    }

    private static string SlackBody(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var title = doc.RootElement.GetProperty("title").GetString();
        var detail = doc.RootElement.GetProperty("detail").GetString();
        return new JsonObject { ["text"] = $"*{title}*\n{detail}" }.ToJsonString();
    }
}
