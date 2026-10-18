using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;

namespace NoiseCut.Core.Evaluation;

/// <summary>
/// Runs every enabled rule over a stream of records (SPEC §6.1, steps 2, 4 and 5).
/// The caller decides which <c>time</c> each record gets: that single choice is what separates
/// processing-time (naive) from event-time (robust) mode. Everything below this class is identical in both.
/// </summary>
public sealed class RuleEngine
{
    private readonly EvaluationSettings _settings;
    private readonly IBaselineSource _baselines;
    private readonly EvaluationContext _ctx;
    private readonly SortedDictionary<string, IRuleEvaluator> _evaluators = new(StringComparer.Ordinal);

    public RuleEngine(EvaluationSettings settings, IBaselineSource baselines, TimeProvider wallClock, string instance, string stage = "order")
    {
        _settings = settings;
        _baselines = baselines;
        _ctx = new EvaluationContext(settings, wallClock, instance, stage);
    }

    public DateTimeOffset Watermark => _ctx.Watermark;

    public IEnumerable<Rule> ActiveRules => _evaluators.Values.Select(e => e.Rule);

    /// <summary>
    /// Replace the rule set. An unchanged (id, version) keeps its state. A new version starts fresh, because
    /// counts gathered under the old conditions would be meaningless under the new ones (SPEC §5).
    /// </summary>
    /// <returns>True if anything changed.</returns>
    public bool SetRules(IEnumerable<Rule> rules)
    {
        var wanted = rules.Where(r => r.Definition.Enabled).ToDictionary(r => r.Id, StringComparer.Ordinal);
        var changed = false;

        foreach (var id in _evaluators.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            _evaluators.Remove(id);
            changed = true;
        }

        foreach (var (id, rule) in wanted)
        {
            if (_evaluators.TryGetValue(id, out var existing) && existing.Rule.Version == rule.Version)
            {
                continue;
            }

            _evaluators[id] = RuleEvaluators.Create(rule, _baselines);
            changed = true;
        }

        return changed;
    }

    public IReadOnlyList<AlertCandidate> Process(OrderEvent evt, DateTimeOffset time, RecordInfo record)
    {
        _ctx.Clear();
        _ctx.Record = record;

        // Step 2: the watermark only moves forward.
        var candidateWatermark = time - _settings.MaxOutOfOrderness;
        var advanced = candidateWatermark > _ctx.Watermark;
        if (advanced)
        {
            _ctx.Watermark = candidateWatermark;
        }

        // Step 4: apply the event to each rule's state (ticks carry no order data).
        if (!evt.IsTick)
        {
            foreach (var evaluator in _evaluators.Values)
            {
                evaluator.OnEvent(evt, time, _ctx);
            }
        }

        // Step 5: watermark-driven work (close panes, fire timers, resolve).
        if (advanced)
        {
            foreach (var evaluator in _evaluators.Values)
            {
                evaluator.OnWatermark(_ctx.Watermark, _ctx);
            }
        }

        return _ctx.Output.ToArray();
    }
}
