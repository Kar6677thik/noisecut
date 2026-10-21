using System.Text.Json.Nodes;

namespace NoiseCut.Experiments.Experiments;

/// <summary>
/// Break #2: event time vs processing time. Two parts, each on a fresh stack, with no real incidents injected,
/// so <b>every alert is false</b>.
///
/// Part A (out-of-order delivery): the simulator holds back 1% of events for up to 60 wall seconds.
///   Hypothesis: a held-back food_ready arrives after its picked_up; the naive absence rule forgets the early pickup and
///   raises a false "stuck pickup".
///
/// Part B (consumer lag): six restaurants run a "slow kitchen" (badly late orders, but at least 4 minutes apart, which is
///   never enough for rule A in event time). After a quiet control period the engine is paused for 120 s (20 sim minutes),
///   then resumed. Hypothesis: the backlog is read in seconds, processing time squeezes it into one 1-minute pane, and
///   rule A fires falsely.
/// </summary>
public static class OutOfOrderExperiment
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Environment =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["engine"] = new Dictionary<string, string> { ["Engine__EmitMode"] = "OnTransition" },
            ["simulator"] = new Dictionary<string, string> { ["Simulator__StartAtLocalHour"] = "12" },
        };

    public static async Task RunAsync(Stack stack, TimeSpan chaosDuration, TimeSpan pauseDuration)
    {
        stack.Run.Results["partA"] = await PartAAsync(stack, chaosDuration);
        stack.Run.Results["partB"] = await PartBAsync(stack, pauseDuration);
    }

    private static async Task<JsonObject> PartAAsync(Stack stack, TimeSpan chaosDuration)
    {
        stack.Run.Log("PART A: out-of-order delivery, no incidents");
        await stack.StartFreshAsync();
        var before = await stack.Client.SimulatorStatusAsync();

        await stack.Client.SetChaosAsync(outOfOrder: 0.01, maxHoldBackSeconds: 60, duplicate: 0);
        stack.Run.Log("chaos on: 1% of events held back up to 60 s");
        await stack.Run.WaitAsync(chaosDuration, "out-of-order traffic");
        await stack.Client.SetChaosAsync(0, 60, 0);
        stack.Run.Log("chaos off");
        await stack.Run.WaitAsync(TimeSpan.FromSeconds(150), "timers armed during chaos (20 sim min = 2 wall min) come due");

        var after = await stack.Client.SimulatorStatusAsync();
        var simHours = (DateTimeOffset.Parse(after["simNow"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)
                        - DateTimeOffset.Parse(before["simNow"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture)).TotalHours;
        var truth = await stack.Client.TruthAsync();
        return Summarise(truth, simHours, after, extra: new JsonObject
        {
            ["eventsHeldBack"] = after["eventsHeld"]!.DeepClone(),
            ["falseAlertsWhere"] = truth["falseAlertsWhere"]?.DeepClone(),
        });
    }

    private static async Task<JsonObject> PartBAsync(Stack stack, TimeSpan pauseDuration)
    {
        stack.Run.Log("PART B: consumer lag with near-miss restaurants, no incidents");
        await stack.StartFreshAsync();
        foreach (var r in new[] { "r-005", "r-015", "r-025", "r-035", "r-045", "r-055" })
        {
            await stack.Client.StartScenarioAsync("slow-kitchen", r, minutes: 120);
        }

        stack.Run.Log("slow-kitchen at 6 restaurants (bad delays, never 5 within 10 min)");
        await stack.Run.WaitAsync(TimeSpan.FromSeconds(180), "control period: engine keeps up");
        var control = await stack.Client.TruthAsync();
        var controlFalse = FalseAlerts(control);

        await stack.Compose.PauseAsync("engine");
        await stack.Run.WaitAsync(pauseDuration, "engine paused: the backlog builds up");
        var lagAtResume = await stack.Compose.GroupLagAsync("noisecut-engine");
        await stack.Compose.UnpauseAsync("engine");
        stack.Run.Log("engine resumed");
        await stack.Run.WaitAsync(TimeSpan.FromSeconds(90), "engine catches up");

        var after = await stack.Client.TruthAsync();
        var afterFalse = FalseAlerts(after);
        return new JsonObject
        {
            ["falseAlertsDuringControl"] = controlFalse.DeepClone(),
            ["falseAlertsAfterPauseTotal"] = afterFalse.DeepClone(),
            ["pauseSeconds"] = pauseDuration.TotalSeconds,
            ["lagRecordsBeforeResume"] = lagAtResume,
            ["scenarios"] = after["scenarios"]!.DeepClone(),
            ["rules"] = after["rules"]!.DeepClone(),
            ["falseAlertsWhere"] = after["falseAlertsWhere"]?.DeepClone(),
        };
    }

    private static JsonObject FalseAlerts(JsonNode truth)
    {
        var byRule = new JsonObject();
        foreach (var rule in truth["rules"]!.AsArray())
        {
            byRule[rule!["ruleId"]!.GetValue<string>()] = rule["falsePositives"]!.GetValue<long>();
        }

        return byRule;
    }

    private static JsonObject Summarise(JsonNode truth, double simHours, JsonNode status, JsonObject extra)
    {
        var result = new JsonObject
        {
            ["simHours"] = Math.Round(simHours, 2),
            ["falseAlertsByRule"] = FalseAlerts(truth),
            ["falseAlertsPerSimHour"] = new JsonObject(),
            ["ordersCreated"] = status["ordersCreated"]!.DeepClone(),
        };
        foreach (var rule in truth["rules"]!.AsArray())
        {
            result["falseAlertsPerSimHour"]![rule!["ruleId"]!.GetValue<string>()] =
                simHours > 0 ? Math.Round(rule["falsePositives"]!.GetValue<long>() / simHours, 1) : null;
        }

        foreach (var (key, value) in extra)
        {
            result[key] = value?.DeepClone();
        }

        return result;
    }
}
