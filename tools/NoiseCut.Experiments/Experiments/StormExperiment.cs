using System.Text.Json.Nodes;

namespace NoiseCut.Experiments.Experiments;

/// <summary>
/// Break #3: alert storm. Eight restaurants in one zone melt down at once, with the fully naive profile
/// (EveryMatch, no dedup, no cooldown, no grouping).
///
/// Hypothesis: one real incident becomes hundreds of candidates and notifications, so a human gets buried.
/// </summary>
public static class StormExperiment
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Environment =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["simulator"] = new Dictionary<string, string> { ["Simulator__StartAtLocalHour"] = "12" },
        };

    public static async Task RunAsync(Stack stack, TimeSpan observe)
    {
        await stack.StartFreshAsync();
        var scenario = await stack.Client.StartScenarioAsync("alert-storm", "z-01", minutes: 15, intensity: 8);
        stack.Run.Log($"alert-storm in z-01 at {scenario["restaurants"]!.AsArray().Count} restaurants");
        await stack.Run.WaitAsync(observe, "the storm plays out");
        await stack.Run.WaitAsync(TimeSpan.FromSeconds(20), "let the outbox drain");

        var window = $"{(int)(observe.TotalSeconds + 60)}s";
        var truth = await stack.Client.TruthAsync();
        var outcome = Stack.Outcome(truth, scenario["id"]!.GetValue<string>());
        var totals = truth["totals"]!;
        stack.Run.Results["restaurantsInStorm"] = scenario["restaurants"]!.AsArray().Count;
        stack.Run.Results["realIncidents"] = 1;
        stack.Run.Results["candidatesIn"] = totals["candidatesIn"]!.DeepClone();
        stack.Run.Results["alerts"] = Stack.Long(outcome, "matchingAlerts");
        stack.Run.Results["notificationsSent"] = totals["notificationsSent"]!.DeepClone();
        stack.Run.Results["duplicateWebhooks"] = totals["sinkDuplicates"]!.DeepClone();
        stack.Run.Results["secondsToFirstAlert"] = Stack.Double(outcome, "secondsToDetect");
        stack.Run.Results["peakCandidatesPerMinute"] = Round(await stack.Client.QueryScalarAsync(
            $"max_over_time((sum(rate(noisecut_candidates_total[1m])) * 60)[{window}:10s])"));
        stack.Run.Results["peakNotificationsPerMinute"] = Round(await stack.Client.QueryScalarAsync(
            $"max_over_time((sum(rate(noisecut_notifications_total[1m])) * 60)[{window}:10s])"));
        stack.Run.Results["notificationsByChannel"] = ToJson(await stack.Client.QueryByLabelAsync(
            "sum by (channel) (noisecut_notifications_total)", "channel"));
        stack.Run.Results["candidatesByRule"] = ToJson(await stack.Client.QueryByLabelAsync(
            "sum by (rule) (noisecut_candidates_total)", "rule"));
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;

    private static JsonObject ToJson(Dictionary<string, double> values)
    {
        var json = new JsonObject();
        foreach (var (k, v) in values)
        {
            json[k] = v;
        }

        return json;
    }
}
