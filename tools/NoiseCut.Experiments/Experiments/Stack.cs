using System.Text.Json.Nodes;
using NoiseCut.Experiments.Harness;

namespace NoiseCut.Experiments.Experiments;

/// <summary>Everything an experiment needs to drive the stack. Each experiment starts from empty volumes, so runs can't contaminate each other.</summary>
public sealed class Stack(Compose compose, NoiseCutClient client, ExperimentRun run)
{
    public Compose Compose { get; } = compose;

    public NoiseCutClient Client { get; } = client;

    public ExperimentRun Run { get; } = run;

    public async Task StartFreshAsync(int engines = 1, TimeSpan? warmup = null)
    {
        Run.Log($"fresh stack: down -v, then up with {engines} engine(s)");
        await Compose.ResetAsync();
        await Compose.UpAsync(engines);
        await Client.WaitUntilReadyAsync(TimeSpan.FromMinutes(3));
        var status = await Client.SimulatorStatusAsync();
        Run.Log($"stack ready; sim time {status["simNow"]}, demand factor {status["demandFactor"]}");
        await Run.WaitAsync(warmup ?? TimeSpan.FromSeconds(30), "warm-up: consumers join their groups, windows fill with normal traffic");
    }

    /// <summary>The scenario outcome row from /api/truth for one injected scenario.</summary>
    public static JsonNode? Outcome(JsonNode truth, string scenarioId) =>
        truth["scenarios"]!.AsArray().FirstOrDefault(s => s?["id"]?.GetValue<string>() == scenarioId);

    public static long Long(JsonNode? node, string property) => node?[property]?.GetValue<long>() ?? 0;

    public static double? Double(JsonNode? node, string property) =>
        node?[property] is JsonValue v && v.TryGetValue<double>(out var d) ? Math.Round(d, 1) : null;
}
