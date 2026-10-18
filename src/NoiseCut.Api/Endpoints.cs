using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NoiseCut.Api.Data;
using NoiseCut.Api.Live;
using NoiseCut.Infrastructure.Webhooks;

namespace NoiseCut.Api;

public static class Endpoints
{
    public static void MapNoiseCutEndpoints(this WebApplication app)
    {
        // --- health -------------------------------------------------------------------------------------------------
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/readyz", async (DashboardQueries q, CancellationToken ct) =>
        {
            try
            {
                return await q.PingAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.Problem("database unavailable", statusCode: 503);
            }
        });

        // --- public, read-only JSON -----------------------------------------------------------------------------------
        var api = app.MapGroup("/api");
        api.MapGet("/alerts", async (DashboardQueries q, string? status, string? rule, int? limit) =>
            Results.Ok(await q.RecentAlertsAsync(Math.Clamp(limit ?? 100, 1, 500), status, rule)));
        api.MapGet("/alerts/{key}", async (string key, DashboardQueries q) =>
            await q.AlertAsync(key) is { } alert
                ? Results.Ok(new { alert, candidates = await q.CandidatesAsync(key), deliveries = await q.DeliveriesAsync(key) })
                : Results.NotFound());
        api.MapGet("/rules", async (DashboardQueries q) => Results.Ok((await q.RulesAsync()).Select(r => new
        {
            r.Id, r.Version, r.Enabled, r.UpdatedAt, r.Hits24h, Definition = JsonDocument.Parse(r.Definition).RootElement,
        })));
        api.MapGet("/stats", async (DashboardQueries q) => Results.Ok(await q.StatsAsync()));
        api.MapGet("/stream", StreamAsync);

        // --- admin (M1: X-Admin-Key or local bypass; cookie login arrives in M4) ---------------------------------------
        var admin = api.MapGroup("").AddEndpointFilter<AdminOnlyFilter>();
        admin.MapPost("/scenarios/{name}", async (string name, HttpRequest request, SimulatorClient sim, CancellationToken ct) =>
            await sim.ForwardAsync($"/scenarios/{Uri.EscapeDataString(name)}", await ReadBodyAsync(request, ct), ct));
        admin.MapPost("/simulator/config", async (HttpRequest request, SimulatorClient sim, CancellationToken ct) =>
            await sim.ForwardAsync("/config", await ReadBodyAsync(request, ct), ct));
        admin.MapGet("/simulator/status", async (SimulatorClient sim, CancellationToken ct) => await sim.GetAsync("/status", ct));
        admin.MapGet("/simulator/scenarios", async (SimulatorClient sim, CancellationToken ct) => await sim.GetAsync("/scenarios", ct));

        // --- the built-in webhook sink -----------------------------------------------------------------------------------
        app.MapPost("/sinks/webhook", ReceiveWebhookAsync).DisableAntiforgery();
    }

    /// <summary>
    /// Server-Sent Events: a long-lived HTTP response that the server keeps writing <c>data: …</c> lines to.
    /// Simpler than WebSockets for one-way server → browser updates, and the browser reconnects automatically.
    /// </summary>
    private static async Task StreamAsync(HttpContext context, FeedHub hub, CancellationToken ct)
    {
        context.Response.Headers.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no"; // ask proxies not to buffer the stream

        var (id, reader) = hub.Subscribe();
        try
        {
            await context.Response.WriteAsync(": connected\n\n", ct);
            await context.Response.Body.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var payload = await reader.ReadAsync(heartbeat.Token);
                    await context.Response.WriteAsync($"data: {payload}\n\n", ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Nothing happened for 15 s: send a comment line so proxies don't close an idle connection.
                    await context.Response.WriteAsync(": heartbeat\n\n", ct);
                }

                await context.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // browser went away
        }
        finally
        {
            hub.Unsubscribe(id);
        }
    }

    /// <summary>
    /// <c>POST /sinks/webhook</c>: reachable publicly, so it trusts nothing without a valid HMAC signature.
    /// Each arrival is recorded under its Idempotency-Key; a second arrival of the same key is a duplicate notification.
    /// </summary>
    private static async Task<IResult> ReceiveWebhookAsync(HttpRequest request, DashboardQueries q, IOptions<ApiOptions> options, ILogger<SimulatorClient> logger, CancellationToken ct)
    {
        var secret = options.Value.WebhookSecret;
        if (string.IsNullOrEmpty(secret))
        {
            return Results.Problem("webhook sink is not configured", statusCode: 503);
        }

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();

        if (!WebhookSignature.Verify(secret, body, request.Headers[WebhookSignature.Header]))
        {
            return Results.Unauthorized();
        }

        var key = request.Headers[WebhookSignature.IdempotencyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            return Results.BadRequest(new { error = "Idempotency-Key header is required" });
        }

        string json;
        try
        {
            using var doc = JsonDocument.Parse(body);
            json = doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "body must be JSON" });
        }

        var count = await q.RecordReceiptAsync(key, json);
        if (count > 1)
        {
            logger.LogWarning("Duplicate webhook receipt #{Count} for {Key}", count, key);
        }

        return Results.Ok(new { received = count, duplicate = count > 1 });
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        return string.IsNullOrWhiteSpace(body) ? "{}" : body;
    }
}
