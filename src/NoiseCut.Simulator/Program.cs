// NoiseCut.Simulator: produces order events in real time, and exposes a small control API (internal only:
// the public Api forwards admin requests here; it is never routed to the internet).
using System.Text.Json.Serialization;
using Npgsql;
using NoiseCut.Core.Serialization;
using NoiseCut.Core.Simulation;
using NoiseCut.Infrastructure.Kafka;
using NoiseCut.Infrastructure.Postgres;
using NoiseCut.Infrastructure.Telemetry;
using NoiseCut.Simulator;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection(SimulatorOptions.Section));
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.Section));
builder.Services.AddNoiseCutTelemetry(builder.Configuration, "noisecut-simulator");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SimulationHost>();
builder.Services.AddHostedService<SimulatorWorker>();

// The truth log is optional: without a database the simulator still runs, it just can't be graded.
var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (!string.IsNullOrEmpty(connectionString))
{
    PostgresSetup.ConfigureDapper();
    builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
}

builder.Services.AddSingleton(sp => new ScenarioLog(sp.GetService<NpgsqlDataSource>(), sp.GetRequiredService<ILogger<ScenarioLog>>()));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    foreach (var converter in NoiseCutJson.Options.Converters)
    {
        o.SerializerOptions.Converters.Add(converter);
    }

    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/readyz", (SimulationHost sim) => sim.IsRunning ? Results.Ok(new { status = "ready" }) : Results.StatusCode(503));
app.MapGet("/status", (SimulationHost sim) => Results.Ok(sim.Status()));
app.MapGet("/scenarios", (SimulationHost sim) => Results.Ok(sim.Scenarios()));

app.MapPost("/scenarios/{name}", async (string name, ScenarioRequest? request, SimulationHost sim, ScenarioLog truth, ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        var scenario = sim.StartScenario(name, request ?? new ScenarioRequest());
        await truth.RecordAsync(scenario, ct);
        logger.LogInformation("Scenario {Name} started for {Target} from {Start:O} to {End:O}", scenario.Name, scenario.Target, scenario.StartsAt, scenario.EndsAt);
        return Results.Created($"/scenarios/{scenario.Id}", scenario);
    }
    catch (ScenarioException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Problem(ex.Message, statusCode: 503);
    }
});

app.MapPost("/config", (SimulatorConfigRequest request, SimulationHost sim) =>
{
    if (request.OrdersPerSimMinute is not (> 0 and <= 5000))
    {
        return Results.BadRequest(new { error = "ordersPerSimMinute must be between 0 and 5000" });
    }

    sim.SetRate(request.OrdersPerSimMinute.Value);
    return Results.Ok(sim.Status());
});

// --- chaos (M2) --------------------------------------------------------------------------------------------------------
app.MapGet("/chaos", (SimulationHost sim) => Results.Ok(sim.Chaos));
app.MapPost("/chaos", (ChaosSettings settings, SimulationHost sim, ILogger<Program> logger) =>
{
    if (settings.Validate() is { } error)
    {
        return Results.BadRequest(new { error });
    }

    sim.Chaos = settings;
    logger.LogWarning("Chaos set: {Chaos}", settings);
    return Results.Ok(settings);
});
app.MapPost("/chaos/late-burst", (LateBurstRequest request, SimulationHost sim, ILogger<Program> logger) =>
{
    if (request.Seconds is <= 0 or > 600)
    {
        return Results.BadRequest(new { error = "seconds must be between 0 and 600" });
    }

    sim.StartLateBurst(TimeSpan.FromSeconds(request.Seconds));
    logger.LogWarning("Late burst: holding all order events for {Seconds}s", request.Seconds);
    return Results.Ok(new { holdingUntil = sim.BurstUntil });
});

app.Run();

internal sealed record SimulatorConfigRequest(double? OrdersPerSimMinute);
