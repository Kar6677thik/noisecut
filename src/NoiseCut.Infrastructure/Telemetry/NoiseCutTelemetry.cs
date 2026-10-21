using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace NoiseCut.Infrastructure.Telemetry;

/// <summary>
/// Metrics wiring shared by every service (SPEC §10): OpenTelemetry SDK → Prometheus text format on an
/// <b>internal-only</b> port (default 9464). Prometheus scrapes that port; it is never routed publicly, and it is deliberately
/// not the Api's public port, so <c>/metrics</c> can't leak through the Cloudflare tunnel.
/// </summary>
public static class NoiseCutTelemetry
{
    // Detection and delivery latency are seconds-scale. OpenTelemetry's default buckets (0, 5, 10, 25 … 10000) assume
    // milliseconds-ish values and would put every observation in the first bucket, so we choose our own.
    private static readonly double[] LatencyBuckets =
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300, 600];

    public static IServiceCollection AddNoiseCutTelemetry(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        var port = configuration.GetValue("Metrics:Port", 9464);
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceInstanceId: Environment.MachineName))
            .WithMetrics(metrics => metrics
                .AddMeter(NoiseCutMetrics.MeterName)
                .AddRuntimeInstrumentation()
                .AddView("noisecut_detection_latency", new ExplicitBucketHistogramConfiguration { Boundaries = LatencyBuckets })
                .AddView("noisecut_delivery_latency", new ExplicitBucketHistogramConfiguration { Boundaries = LatencyBuckets })
                .AddView("noisecut_checkpoint_duration", new ExplicitBucketHistogramConfiguration { Boundaries = [0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5] })
                .AddPrometheusHttpListener(o =>
                {
                    o.Port = port;

                    // The exporter binds to "localhost" by default, which Prometheus in another container can't reach.
                    // HttpListener's wildcard prefix "http://*:port/" accepts any Host header; the exporter's Host option
                    // must be a valid URI host, so the wildcard is added on the listener itself.
                    o.ConfigureHttpListener = (_, listener) => listener.Prefixes.Add($"http://*:{port}/");
                }));
        return services;
    }
}
