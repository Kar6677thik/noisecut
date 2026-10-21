using System.Diagnostics;
using System.Text;

namespace NoiseCut.Experiments.Harness;

/// <summary>
/// Runs <c>docker compose</c> against the local stack. Per-experiment settings go into a generated override file
/// (Compose merges <c>environment</c> maps), so the committed compose file never changes for an experiment.
/// </summary>
public sealed class Compose
{
    private readonly string _baseFile;
    private readonly string _overrideFile;
    private readonly Action<string> _log;

    public Compose(string repoRoot, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> serviceEnvironment, Action<string> log)
    {
        _baseFile = Path.Combine(repoRoot, "deploy", "compose", "docker-compose.yml");
        _overrideFile = Path.Combine(Path.GetTempPath(), $"noisecut-experiment-{Environment.ProcessId}.override.yml");
        _log = log;

        var yaml = new StringBuilder("services:\n");
        foreach (var (service, env) in serviceEnvironment)
        {
            yaml.Append("  ").Append(service).Append(":\n    environment:\n");
            foreach (var (key, value) in env)
            {
                yaml.Append("      ").Append(key).Append(": \"").Append(value).Append("\"\n");
            }
        }

        if (serviceEnvironment.Count == 0)
        {
            yaml.Append("  api: {}\n");
        }

        File.WriteAllText(_overrideFile, yaml.ToString());
    }

    public Task ResetAsync() => RunAsync("down -v --remove-orphans");

    public Task BuildAsync() => RunAsync("build", TimeSpan.FromMinutes(15));

    public Task UpAsync(int engines = 1) => RunAsync($"up -d --scale engine={engines}", TimeSpan.FromMinutes(5));

    /// <summary>SIGKILL: no shutdown hooks, no final commit. A crash, not a polite stop.</summary>
    public Task KillAsync(string service) => RunAsync($"kill {service}");

    public Task StartAsync(string service) => RunAsync($"start {service}");

    /// <summary>Freezes the processes (cgroup freezer): the consumer stops reading but keeps its group membership for a while.</summary>
    public Task PauseAsync(string service) => RunAsync($"pause {service}");

    public Task UnpauseAsync(string service) => RunAsync($"unpause {service}");

    /// <summary>Total lag of a consumer group, as Redpanda itself reports it (works even while the consumer is frozen).</summary>
    public async Task<long?> GroupLagAsync(string group)
    {
        var output = await RunAsync($"exec -T redpanda rpk group describe {group}");
        var line = output.Split('\n').FirstOrDefault(l => l.StartsWith("TOTAL-LAG", StringComparison.Ordinal));
        return line is not null && long.TryParse(line["TOTAL-LAG".Length..].Trim(), out var lag) ? lag : null;
    }

    public async Task<string> RunAsync(string arguments, TimeSpan? timeout = null)
    {
        var args = $"compose -f \"{_baseFile}\" -f \"{_overrideFile}\" {arguments}";
        _log($"$ docker compose {arguments}");
        using var process = Process.Start(new ProcessStartInfo("docker", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(cts.Token);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker {args} failed ({process.ExitCode}):\n{await stderr}\n{await stdout}");
        }

        return await stdout;
    }
}
