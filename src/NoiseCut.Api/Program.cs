// NoiseCut.Api: the dashboard (Razor Pages), JSON/SSE APIs, the webhook sink and admin forwarding to the simulator.
using Microsoft.Extensions.Options;
using NoiseCut.Api;
using NoiseCut.Api.Data;
using NoiseCut.Api.Live;
using NoiseCut.Infrastructure;
using NoiseCut.Infrastructure.Postgres;
using NoiseCut.Infrastructure.Telemetry;

var builder = WebApplication.CreateBuilder(args);
var profile = builder.Configuration.ApplyProfile();

builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection(ApiOptions.Section));
builder.Services.AddNoiseCutPostgres(builder.Configuration);
builder.Services.AddNoiseCutTelemetry(builder.Configuration, "noisecut-api");
builder.Services.AddSingleton<DashboardQueries>();
builder.Services.AddSingleton<AdminGuard>();
builder.Services.AddSingleton<FeedHub>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FeedHub>());
builder.Services.AddHttpClient<SimulatorClient>((sp, http) =>
{
    http.BaseAddress = new Uri(sp.GetRequiredService<IOptions<ApiOptions>>().Value.SimulatorUrl);
    http.Timeout = TimeSpan.FromSeconds(5);
});
builder.Services.AddSingleton(new ProfileInfo(profile));
builder.Services.AddRazorPages();

var app = builder.Build();

if (app.Services.GetRequiredService<AdminGuard>().BypassEnabled)
{
    app.Logger.LogWarning("Admin bypass is ON: every visitor is treated as admin. Local development only.");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();
app.MapRazorPages();
app.MapNoiseCutEndpoints();

app.Run();

/// <summary>Which config profile is active; shown in the page footer so nobody mistakes naive mode for the real thing.</summary>
public sealed record ProfileInfo(string Name);
