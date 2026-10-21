using System.Text.Json.Nodes;

namespace NoiseCut.Experiments.Experiments;

/// <summary>
/// Break #1: kill the engine (SIGKILL) while an incident is unfolding, then start it again.
///
/// Hypothesis: with state in memory and offsets auto-committed on a timer, a crash
/// (a) throws away the partial window, so an incident is detected late or missed, and
/// (b) replays the records after the last auto-commit, so the same incident is alerted on again.
///
/// To isolate the crash effect, EmitMode=OnTransition (EveryMatch noise is Break #3). Everything else is naive.
/// Round 0 is a control without a kill.
/// </summary>
public static class RestartExperiment
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Environment =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["engine"] = new Dictionary<string, string> { ["Engine__EmitMode"] = "OnTransition" },
            ["simulator"] = new Dictionary<string, string> { ["Simulator__StartAtLocalHour"] = "12" },
        };

    public static async Task RunAsync(Stack stack, int rounds, TimeSpan roundDuration, double? rate = null)
    {
        // One restaurant per zone, so rounds never share a partition-local window by accident.
        // A different restaurant every round (cycling through zones), so cooldowns and windows never carry over between rounds.
        var restaurants = Enumerable.Range(0, 60).Select(i => $"r-{(i % 6 * 10) + (i / 6) + 1:000}").ToArray();
        int[] killAfterSeconds = [15, 30, 45, 60, 90];
        await stack.StartFreshAsync();
        if (rate is { } r)
        {
            await stack.Client.SetRateAsync(r);
            stack.Run.Log($"load: {r} orders/sim-min");
        }
        var plan = new List<(int Round, string Restaurant, int? KillAfter, string ScenarioId)>();

        for (var round = 0; round <= rounds; round++)
        {
            var restaurant = restaurants[round % restaurants.Length];
            int? killAfter = round == 0 ? null : killAfterSeconds[(round - 1) % killAfterSeconds.Length];
            var started = DateTimeOffset.UtcNow;
            var scenario = await stack.Client.StartScenarioAsync("kitchen-meltdown", restaurant);
            var id = scenario["id"]!.GetValue<string>();
            stack.Run.Log($"round {round}: kitchen-meltdown at {restaurant}" + (killAfter is null ? " (control, no kill)" : $", kill after {killAfter}s"));
            plan.Add((round, restaurant, killAfter, id));

            if (killAfter is { } k)
            {
                await Task.Delay(TimeSpan.FromSeconds(k));
                await stack.Compose.KillAsync("engine");
                await stack.Run.WaitAsync(TimeSpan.FromSeconds(5), "engine is down");
                await stack.Compose.StartAsync("engine");
                stack.Run.Log("engine started again");
            }

            var remaining = roundDuration - (DateTimeOffset.UtcNow - started);
            if (remaining > TimeSpan.Zero)
            {
                await stack.Run.WaitAsync(remaining, "let the meltdown play out");
            }
        }

        await stack.Run.WaitAsync(TimeSpan.FromSeconds(30), "let the notifier drain its outbox");

        var truth = await stack.Client.TruthAsync();
        var table = new JsonArray();
        long missed = 0, extraAlerts = 0;
        foreach (var (round, restaurant, killAfter, id) in plan)
        {
            var outcome = Stack.Outcome(truth, id);
            var alerts = Stack.Long(outcome, "matchingAlerts");
            missed += alerts == 0 ? 1 : 0;
            extraAlerts += Math.Max(0, alerts - 1);
            table.Add(new JsonObject
            {
                ["round"] = round,
                ["restaurant"] = restaurant,
                ["killAfterSeconds"] = killAfter,
                ["detected"] = alerts > 0,
                ["secondsToDetect"] = Stack.Double(outcome, "secondsToDetect"),
                ["alertsForIncident"] = alerts,
                ["candidates"] = Stack.Long(outcome, "candidates"),
            });
        }

        var totals = truth["totals"]!;
        stack.Run.Results["rounds"] = table;
        stack.Run.Results["kills"] = rounds;
        stack.Run.Results["fencedCheckpoints"] = await stack.Client.QueryScalarAsync("sum(noisecut_checkpoints_fenced_total) or vector(0)");
        stack.Run.Results["lateEventsDropped"] = await stack.Client.QueryScalarAsync("sum(noisecut_late_events_total{outcome=\"dropped\"}) or vector(0)");
        stack.Run.Results["missedIncidents"] = missed;
        stack.Run.Results["extraAlertsForSameIncident"] = extraAlerts;
        stack.Run.Results["sinkReceipts"] = totals["sinkReceipts"]!.DeepClone();
        stack.Run.Results["duplicateWebhooks"] = totals["sinkDuplicates"]!.DeepClone();
        stack.Run.Results["falseAlertsByRule"] = truth["rules"]!.DeepClone();
    }
}
