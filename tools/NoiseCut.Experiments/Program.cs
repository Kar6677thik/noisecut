// NoiseCut experiment harness (ADR-0013).
//   dotnet run --project tools/NoiseCut.Experiments -- <restart|out-of-order|storm|scale|all> [--rounds N] [--no-build]
// Each experiment starts the local Compose stack from empty volumes, drives it, grades the result against the simulator's
// truth log, and writes docs/experiments/results/<experiment>-<timestamp>.json.
using System.Text.Json.Nodes;
using NoiseCut.Experiments.Experiments;
using NoiseCut.Experiments.Harness;

var command = args.FirstOrDefault() ?? "help";
var rounds = ArgInt("--rounds", 5);
var build = !args.Contains("--no-build");
var profile = Environment.GetEnvironmentVariable("NOISECUT_PROFILE") ?? "naive";
var adminKey = Environment.GetEnvironmentVariable("NOISECUT_ADMIN_KEY") ?? "local-dev-admin-key";
var repoRoot = FindRepoRoot();

var experiments = command == "all" ? ["restart", "out-of-order", "storm", "scale", "load"] : new[] { command };
if (experiments.Any(e => e is not ("restart" or "out-of-order" or "storm" or "scale" or "load")))
{
    Console.WriteLine("usage: NoiseCut.Experiments <restart|out-of-order|storm|scale|load|all> [--rounds N] [--round-seconds S] [--zone z-0N] [--rate R] [--no-build]   (profile: NOISECUT_PROFILE=naive|robust)");
    return 2;
}

foreach (var name in experiments)
{
    var environment = name switch
    {
        "restart" => RestartExperiment.Environment,
        "out-of-order" => OutOfOrderExperiment.Environment,
        "storm" => StormExperiment.Environment,
        "load" => LoadExperiment.Environment,
        _ => ScaleExperiment.Environment,
    };

    var configuration = new JsonObject { ["profile"] = profile, ["serviceOverrides"] = ToJson(environment) };
    var run = new ExperimentRun($"{name}-{profile}", repoRoot, configuration);
    var compose = new Compose(repoRoot, environment, run.Log);
    using var client = new NoiseCutClient("http://localhost:8080", "http://localhost:9090", adminKey);
    var stack = new Stack(compose, client, run);

    if (build)
    {
        run.Log("building images");
        await compose.BuildAsync();
        build = false;
    }

    run.Log($"=== {name} (profile {profile}) ===");
    try
    {
        switch (name)
        {
            case "restart":
                configuration["rounds"] = rounds;
                var roundSeconds = ArgInt("--round-seconds", 150);
                double? restartRate = ArgString("--rate") is { } rr ? double.Parse(rr, System.Globalization.CultureInfo.InvariantCulture) : null;
                configuration["roundSeconds"] = roundSeconds;
                configuration["rate"] = restartRate;
                await RestartExperiment.RunAsync(stack, rounds, TimeSpan.FromSeconds(roundSeconds), restartRate);
                break;
            case "out-of-order":
                configuration["chaosSeconds"] = 360;
                configuration["pauseSeconds"] = 120;
                await OutOfOrderExperiment.RunAsync(stack, TimeSpan.FromSeconds(360), TimeSpan.FromSeconds(120));
                break;
            case "storm":
                configuration["observeSeconds"] = 210;
                await StormExperiment.RunAsync(stack, TimeSpan.FromSeconds(210));
                break;
            case "load":
                configuration["levels"] = "300, 600, 1200 orders/sim-min";
                await LoadExperiment.RunAsync(stack);
                break;
            case "scale":
                configuration["observeSeconds"] = 360;
                var zone = ArgString("--zone") ?? "z-02";
                double? onlyRate = ArgString("--rate") is { } rate ? double.Parse(rate, System.Globalization.CultureInfo.InvariantCulture) : null;
                configuration["zone"] = zone;
                configuration["rate"] = onlyRate;
                await ScaleExperiment.RunAsync(stack, TimeSpan.FromSeconds(360), zone, onlyRate);
                break;
        }
    }
    catch (Exception ex)
    {
        run.Results["error"] = ex.ToString();
        run.Log($"FAILED: {ex.Message}");
    }

    var path = run.Save();
    Console.WriteLine();
    Console.WriteLine($"results: {path}");
    Console.WriteLine(run.Results.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}

return 0;

string? ArgString(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

int ArgInt(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NoiseCut.slnx")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? throw new InvalidOperationException("run from inside the repository");
}

static JsonObject ToJson(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> env)
{
    var json = new JsonObject();
    foreach (var (service, vars) in env)
    {
        var o = new JsonObject();
        foreach (var (k, v) in vars)
        {
            o[k] = v;
        }

        json[service] = o;
    }

    return json;
}
