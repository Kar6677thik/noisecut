using System.Globalization;
using System.Text.Json.Nodes;

namespace NoiseCut.Experiments.Experiments;

/// <summary>
/// Experiment 5: detection latency under load. The same stack at 1×, 2× and 4× the default order rate (300, 600, 1200 orders/sim-min,
/// i.e. roughly 300, 600 and 1,200 events per wall second plus ticks). At each level a fresh burst of incidents is injected and the run
/// reports detection latency p50/p95/p99 (detectedAt − triggerProducedAt, wall clock), plus consumer lag and throughput.
///
/// Latency is computed from the histogram's increase during each level's observation window only, so levels don't mix.
/// </summary>
public static class LoadExperiment
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Environment =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["simulator"] = new Dictionary<string, string> { ["Simulator__StartAtLocalHour"] = "12" },
        };

    public static async Task RunAsync(Stack stack)
    {
        await stack.StartFreshAsync();
        var levels = new JsonArray();
        var restaurant = 0;
        foreach (var (label, rate) in new[] { ("1x", 300.0), ("2x", 600.0), ("4x", 1200.0) })
        {
            await stack.Client.SetRateAsync(rate);
            await stack.Run.WaitAsync(TimeSpan.FromSeconds(45), $"{label}: settle at {rate} orders/sim-min");

            // Incidents to detect: two meltdowns and a rider shortage, in different zones each level.
            for (var i = 0; i < 2; i++)
            {
                await stack.Client.StartScenarioAsync("kitchen-meltdown", $"r-{(restaurant++ % 6 * 10) + 5:000}");
            }

            await stack.Client.StartScenarioAsync("rider-shortage", $"z-0{(restaurant % 6) + 1}");
            var window = TimeSpan.FromSeconds(150);
            await stack.Run.WaitAsync(window, $"{label}: observe");
            var w = $"{(int)window.TotalSeconds}s";

            async Task<double?> Quantile(double q) => Round(await stack.Client.QueryScalarAsync(
                string.Create(CultureInfo.InvariantCulture, $"histogram_quantile({q}, sum by (le) (increase(noisecut_detection_latency_seconds_bucket[{w}])))")), 3);

            var level = new JsonObject
            {
                ["level"] = label,
                ["ordersPerSimMinute"] = rate,
                ["eventsPerSecond"] = Round(await stack.Client.QueryScalarAsync($"sum(rate(noisecut_events_consumed_total{{stage=\"order\"}}[{w}]))"), 0),
                ["candidates"] = Round(await stack.Client.QueryScalarAsync($"sum(increase(noisecut_candidates_total[{w}]))"), 0),
                ["detectionP50Seconds"] = await Quantile(0.50),
                ["detectionP95Seconds"] = await Quantile(0.95),
                ["detectionP99Seconds"] = await Quantile(0.99),
                ["maxConsumerLagRecords"] = Round(await stack.Client.QueryScalarAsync($"max_over_time(sum(noisecut_consumer_lag_records{{stage=\"order\"}})[{w}:5s])"), 0),
                ["maxRecordAgeSeconds"] = Round(await stack.Client.QueryScalarAsync($"max_over_time(max(noisecut_record_age_seconds{{stage=\"order\"}})[{w}:5s])"), 2),
                ["checkpointP95Seconds"] = Round(await stack.Client.QueryScalarAsync($"histogram_quantile(0.95, sum by (le) (increase(noisecut_checkpoint_duration_seconds_bucket{{stage=\"order\"}}[{w}])))"), 4),
            };
            stack.Run.Log($"{label}: {level.ToJsonString()}");
            levels.Add(level);
        }

        stack.Run.Results["levels"] = levels;
        stack.Run.Results["truth"] = (await stack.Client.TruthAsync())["rules"]!.DeepClone();
    }

    private static double? Round(double? v, int digits) => v is { } x ? Math.Round(x, digits) : null;
}
