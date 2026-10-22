using Microsoft.Extensions.Options;
using NoiseCut.Core.Rules;
using NoiseCut.Infrastructure.Postgres;

namespace NoiseCut.Engine;

/// <summary>
/// Polls PostgreSQL for rules and baselines in the background. The consume loop picks up new rules between records,
/// so rule state is only ever touched by one thread. (LISTEN/NOTIFY hot reload arrives with rule editing, after M4: SPEC §8.3.)
/// </summary>
public sealed class RuleFeed(RuleStore store, BaselineCache baselines, IOptions<EngineOptions> options, ILogger<RuleFeed> logger)
    : BackgroundService
{
    private volatile RuleSnapshot _current = new(0, []);

    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The newest rule set and a version number that changes whenever it does. Each consume loop remembers the version it
    /// applied and calls SetRules between records when it sees a newer one, so rule state is only touched by its own thread.
    /// </summary>
    public RuleSnapshot Current => _current;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastBaselineRefresh = DateTimeOffset.MinValue;
        IReadOnlyList<Rule>? last = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastBaselineRefresh >= options.Value.BaselineRefreshInterval)
                {
                    await baselines.RefreshAsync(stoppingToken);
                    lastBaselineRefresh = DateTimeOffset.UtcNow;
                    logger.LogInformation("Loaded {Count} baseline hours", baselines.Count);
                }

                var result = await store.LoadAsync(stoppingToken);
                foreach (var problem in result.Problems)
                {
                    logger.LogError("Skipping invalid rule: {Problem}", problem);
                }

                if (last is null || !SameVersions(last, result.Rules))
                {
                    logger.LogInformation("Rules: {Rules}", string.Join(", ", result.Rules.Select(r => $"{r.Id} v{r.Version}{(r.Definition.Enabled ? "" : " (disabled)")}")));
                    _current = new RuleSnapshot(_current.Version + 1, result.Rules);
                    last = result.Rules;
                }

                Ready.TrySetResult();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not load rules; retrying");
            }

            await Task.Delay(Ready.Task.IsCompleted ? options.Value.RuleRefreshInterval : TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    public sealed record RuleSnapshot(int Version, IReadOnlyList<Rule> Rules);

    private static bool SameVersions(IReadOnlyList<Rule> a, IReadOnlyList<Rule> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Id == p.Second.Id && p.First.Version == p.Second.Version && p.First.Definition.Enabled == p.Second.Definition.Enabled);
}
