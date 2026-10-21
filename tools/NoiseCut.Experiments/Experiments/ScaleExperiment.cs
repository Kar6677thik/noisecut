using System.Text.Json.Nodes;

namespace NoiseCut.Experiments.Experiments;

/// <summary>
/// Break #4: scale the engine to 3 consumers <b>without</b> the zone stage.
///
/// Orders are keyed by restaurant, so one zone's orders are spread over several partitions, and therefore over several
/// engines. Each engine computes rule B (zone cancellation rate) from its own share.
/// Hypothesis: each engine sees only part of each zone, so the 300-order guard is reached later or never, and the spike is
/// detected late or missed. Rules A and C are per restaurant/order and are unaffected.
///
/// Design notes (learned from a first, discarded run):
/// - The sim clock starts exactly on a UTC hour (16:30 IST = 11:00 UTC), so the spike covers one full hour cell.
///   Snapshots taken right after an hour boundary compare counts that have just reset.
/// - Per-engine counts are reported as <b>shares</b> of the zone. Processing-time hours are not exactly aligned with the
///   simulator's event-time hours, so raw counts from the two sides differ a little for that reason alone.
/// - Two load levels: at 300 orders/sim-min every engine with a decent share still passes the guard; at 80 a single engine
///   passes it (≈400 orders per zone-hour) but an engine holding only part of the zone may not.
/// </summary>
public static class ScaleExperiment
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Environment =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["engine"] = new Dictionary<string, string> { ["Engine__EmitMode"] = "OnTransition" },
            ["simulator"] = new Dictionary<string, string> { ["Simulator__StartAtLocalHour"] = "16.5" },
        };

    public static async Task RunAsync(Stack stack, TimeSpan observe, string zone = "z-02", double? onlyRate = null)
    {
        var runs = new JsonArray();
        var plan = onlyRate is { } r ? new[] { (r, 1), (r, 3) } : new[] { (300.0, 1), (300.0, 3), (80.0, 1), (80.0, 3) };
        foreach (var (rate, engines) in plan)
        {
            stack.Run.Log($"RUN with {engines} engine(s) at {rate} orders/sim-min, spike in {zone}");
            await stack.StartFreshAsync(engines, TimeSpan.FromSeconds(15));
            await stack.Client.SetRateAsync(rate);
            var injectedAt = DateTimeOffset.UtcNow;
            var scenario = await stack.Client.StartScenarioAsync("zone-cancel-spike", zone, minutes: 90, intensity: 3);
            var id = scenario["id"]!.GetValue<string>();

            // 200 s ≈ 33 sim minutes after injection, about 35 minutes into the hour cell: far from either boundary.
            await stack.Run.WaitAsync(TimeSpan.FromSeconds(200), "zone-hour counts build up");
            var truthCount = await stack.Client.QueryScalarAsync("max(noisecut_sim_zone_hour_orders{zone=\"" + zone + "\",kind=\"placed\"})");
            var perEngine = await stack.Client.QueryByLabelAsync("max by (instance) (noisecut_engine_zone_hour_orders{zone=\"" + zone + "\",kind=\"placed\"})", "instance");
            var seenTotal = perEngine.Values.Sum();

            JsonNode? outcome = null;
            while (DateTimeOffset.UtcNow - injectedAt < observe)
            {
                outcome = Stack.Outcome(await stack.Client.TruthAsync(), id);
                if (Stack.Long(outcome, "matchingAlerts") > 0)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(10));
            }

            outcome = Stack.Outcome(await stack.Client.TruthAsync(), id);
            var alerts = (await stack.Client.AlertsAsync("zone-cancellation-spike")).AsArray()
                .Where(a => a?["entityKey"]?.GetValue<string>() == zone)
                .Select(a => a!["detail"]!.GetValue<string>())
                .ToArray();

            var shares = new JsonArray();
            foreach (var (instance, count) in perEngine.OrderByDescending(p => p.Value))
            {
                shares.Add(new JsonObject
                {
                    ["instance"] = instance,
                    ["placedSeen"] = count,
                    ["shareOfZone"] = seenTotal > 0 ? Math.Round(count / seenTotal, 2) : null,
                });
            }

            var run = new JsonObject
            {
                ["zone"] = zone,
                ["ordersPerSimMinute"] = rate,
                ["engines"] = engines,
                ["detected"] = Stack.Long(outcome, "matchingAlerts") > 0,
                ["secondsToDetect"] = Stack.Double(outcome, "secondsToDetect"),
                ["alerts"] = Stack.Long(outcome, "matchingAlerts"),
                ["alertDetails"] = new JsonArray(alerts.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                ["truthPlacedThisHourAtSnapshot"] = truthCount,
                ["enginesWithZoneData"] = perEngine.Count,
                ["engineViewsAtSnapshot"] = shares,
                ["observeSeconds"] = observe.TotalSeconds,
            };
            stack.Run.Log($"{engines} engine(s) @ {rate}: detected={run["detected"]} after {run["secondsToDetect"]}s; truth {truthCount}; views {shares.ToJsonString()}");
            runs.Add(run);
        }

        stack.Run.Results["runs"] = runs;
    }
}
