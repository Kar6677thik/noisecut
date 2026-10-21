// NoiseCut.Engine: reads order-events, evaluates rules, writes alert-candidates.
using NoiseCut.Engine;
using NoiseCut.Infrastructure;
using NoiseCut.Infrastructure.Kafka;
using NoiseCut.Infrastructure.Postgres;
using NoiseCut.Infrastructure.Telemetry;

var builder = Host.CreateApplicationBuilder(args);
var profile = builder.Configuration.ApplyProfile();

builder.Services.Configure<EngineOptions>(builder.Configuration.GetSection(EngineOptions.Section));
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.Section));
builder.Services.AddNoiseCutPostgres(builder.Configuration);
builder.Services.AddNoiseCutTelemetry(builder.Configuration, "noisecut-engine");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RuleStore>();
builder.Services.AddSingleton<BaselineCache>();
builder.Services.AddSingleton<RuleFeed>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RuleFeed>());
builder.Services.AddSingleton<CheckpointStore>();

// Naive (M1): one in-memory engine per process. Robust (M3): per-partition, checkpointed order stage + zone stage.
if (builder.Configuration.GetValue<bool>("Engine:ZoneStage"))
{
    builder.Services.AddHostedService<NoiseCut.Engine.Robust.OrderStage>();
    builder.Services.AddHostedService<NoiseCut.Engine.Robust.ZoneStage>();
}
else
{
    builder.Services.AddHostedService<EngineWorker>();
}

var host = builder.Build();
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NoiseCut").LogInformation("Profile: {Profile}", profile);
host.Run();
