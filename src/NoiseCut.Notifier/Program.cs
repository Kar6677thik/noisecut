// NoiseCut.Notifier: alert-candidates → alerts + outbox → channels.
using Microsoft.Extensions.Options;
using NoiseCut.Infrastructure;
using NoiseCut.Infrastructure.Kafka;
using NoiseCut.Infrastructure.Postgres;
using NoiseCut.Infrastructure.Telemetry;
using NoiseCut.Notifier;

var builder = Host.CreateApplicationBuilder(args);
var profile = builder.Configuration.ApplyProfile();

builder.Services.Configure<NotifierOptions>(builder.Configuration.GetSection(NotifierOptions.Section));
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection(KafkaOptions.Section));
builder.Services.AddNoiseCutPostgres(builder.Configuration);
builder.Services.AddNoiseCutTelemetry(builder.Configuration, "noisecut-notifier");
builder.Services.AddSingleton<RuleStore>();
builder.Services.AddSingleton<RuleCatalog>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RuleCatalog>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CandidateIntake>();
builder.Services.AddHostedService<IntakeWorker>();

builder.Services.AddHttpClient(WebhookChannel.HttpClientName, (sp, client) =>
    client.Timeout = sp.GetRequiredService<IOptions<NotifierOptions>>().Value.Webhook.Timeout);
builder.Services.AddSingleton<IChannel, DashboardChannel>();
builder.Services.AddSingleton<IChannel, WebhookChannel>();
builder.Services.AddSingleton<IChannel, EmailChannel>();
builder.Services.AddHostedService<DeliveryDispatcher>();
builder.Services.AddHostedService<GroupFlusher>();
builder.Services.AddHostedService<Escalator>();

var host = builder.Build();
host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NoiseCut").LogInformation("Profile: {Profile}", profile);
host.Run();
