using NoiseCut.Core.Rules;
using NoiseCut.Infrastructure.Postgres;

namespace NoiseCut.Notifier;

/// <summary>
/// Rule names and notify settings, refreshed every 30 s. Candidates carry the rule id and version, not the rule itself,
/// so the notifier looks up channels here.
/// </summary>
public sealed class RuleCatalog(RuleStore store, ILogger<RuleCatalog> logger) : BackgroundService
{
    private volatile IReadOnlyDictionary<string, Rule> _rules = new Dictionary<string, Rule>();

    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Rule? Find(string ruleId) => _rules.GetValueOrDefault(ruleId);

    public IReadOnlyCollection<Rule> All() => [.. _rules.Values];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await store.LoadAsync(stoppingToken);
                _rules = result.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
                Ready.TrySetResult();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not load rules; retrying");
            }

            await Task.Delay(Ready.Task.IsCompleted ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
