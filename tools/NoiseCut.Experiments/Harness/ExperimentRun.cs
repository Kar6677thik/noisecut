using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NoiseCut.Experiments.Harness;

/// <summary>
/// One experiment run: a timestamped log on the console, and a JSON results file with everything needed to reproduce it
/// (commit, machine, configuration, every step with its wall-clock time, and the measured numbers).
/// </summary>
public sealed class ExperimentRun
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly JsonArray _steps = [];

    public ExperimentRun(string name, string repoRoot, JsonObject configuration)
    {
        Name = name;
        RepoRoot = repoRoot;
        Configuration = configuration;
        StartedAt = DateTimeOffset.UtcNow;
    }

    public string Name { get; }

    public string RepoRoot { get; }

    public JsonObject Configuration { get; }

    public DateTimeOffset StartedAt { get; }

    public JsonObject Results { get; } = [];

    public void Log(string message)
    {
        var line = $"[{_clock.Elapsed:mm\\:ss}] {message}";
        Console.WriteLine(line);
        _steps.Add(new JsonObject { ["t"] = Math.Round(_clock.Elapsed.TotalSeconds, 1), ["step"] = message });
    }

    public async Task WaitAsync(TimeSpan duration, string why)
    {
        Log($"waiting {duration.TotalSeconds:0}s: {why}");
        await Task.Delay(duration);
    }

    public string Save()
    {
        var dir = Path.Combine(RepoRoot, "docs", "experiments", "results");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Name}-{StartedAt:yyyyMMdd-HHmmss}.json");
        var document = new JsonObject
        {
            ["experiment"] = Name,
            ["startedAt"] = StartedAt,
            ["durationSeconds"] = Math.Round(_clock.Elapsed.TotalSeconds),
            ["commit"] = Git("rev-parse --short HEAD") + (string.IsNullOrEmpty(Git("status --porcelain --untracked-files=no")) ? "" : "+dirty"),
            ["machine"] = new JsonObject
            {
                ["os"] = Environment.OSVersion.ToString(),
                ["logicalCpus"] = Environment.ProcessorCount,
                ["docker"] = Docker("info --format \"{{.NCPU}} CPUs, {{.MemTotal}} bytes, server {{.ServerVersion}}\""),
            },
            ["configuration"] = Configuration.DeepClone(),
            ["results"] = Results.DeepClone(),
            ["steps"] = _steps.DeepClone(),
        };
        File.WriteAllText(path, document.ToJsonString(Pretty) + "\n");
        return path;
    }

    private string Git(string args) => Run("git", args);

    private string Docker(string args) => Run("docker", args);

    private string Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = RepoRoot })!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return "unknown";
        }
    }
}
