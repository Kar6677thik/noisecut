// NoiseCut.Simulator: produces order events in real time, and exposes a small control API (internal only:
// the public Api forwards admin requests here; it is never routed to the internet).
using System.Text.Json.Serialization;
using NoiseCut.Core.Serialization;
using NoiseCut.Core.Simulation;
using NoiseCut.Infrastructure.Kafka;
using NoiseCut.Simulator;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection(SimulatorOptions.Section));
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.Section));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SimulationHost>();
builder.Services.AddHostedService<SimulatorWorker>();
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

app.MapPost("/scenarios/{name}", (string name, ScenarioRequest? request, SimulationHost sim, ILogger<Program> logger) =>
{
    try
    {
        var scenario = sim.StartScenario(name, request ?? new ScenarioRequest());
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

app.Run();

internal sealed record SimulatorConfigRequest(double? OrdersPerSimMinute);
