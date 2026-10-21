using System.Text.Json.Nodes;
using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;

namespace NoiseCut.Core.Evaluation;

/// <summary>
/// Runs every enabled rule over a stream of records (SPEC §6.1, steps 2–5).
/// The caller decides which <c>time</c> each record gets: that single choice is what separates
/// processing-time (naive) from event-time (robust) mode. Everything below this class is identical in both.
///
/// In the robust order stage there is one <see cref="RuleEngine"/> <b>per Kafka partition</b>, so its state can be
/// checkpointed with that partition's offset and handed to another engine instance on a rebalance (ADR-0005).
/// </summary>
public sealed class RuleEngine
{
    public const int StateVersion = 1;

    private readonly EvaluationSettings _settings;
    private readonly IBaselineSource _baselines;
    private readonly EvaluationContext _ctx;
    private readonly EngineRole _role;
    private readonly int _partition;
    private readonly SortedDictionary<string, IRuleEvaluator> _evaluators = new(StringComparer.Ordinal);

    public RuleEngine(
        EvaluationSettings settings,
        IBaselineSource baselines,
        TimeProvider wallClock,
        string instance,
        string stage = "order",
        EngineRole role = EngineRole.Single,
        int partition = 0)
    {
        _settings = settings;
        _baselines = baselines;
        _role = role;
        _partition = partition;
        _ctx = new EvaluationContext(settings, wallClock, instance, stage);
    }

    public DateTimeOffset Watermark => _ctx.Watermark;

    public IEnumerable<Rule> ActiveRules => _evaluators.Values.Select(e => e.Rule);

    /// <summary>Zone partials produced by the last <see cref="Process"/> call (order role only).</summary>
    public IReadOnlyList<ZonePartial> LastPartials => _ctx.Partials.ToArray();

    /// <summary>Per-(zone, hour) numerator/denominator counts held for zone rules by <i>this</i> engine instance.</summary>
    public IReadOnlyList<ZoneHourCount> ZoneHourCounts() =>
    [
        .. _evaluators.Values.OfType<RatioVsBaselineEvaluator>().SelectMany(e => e.Counts()),
        .. _evaluators.Values.OfType<ZonePartialEmitter>().SelectMany(e => e.Counts()),
    ];

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

            _evaluators[id] = RuleEvaluators.Create(rule, _baselines, _role, _partition);
            changed = true;
        }

        return changed;
    }

    /// <summary>SPEC §6.1 step 3: is this event too far behind the watermark to be applied?</summary>
    public Lateness Classify(DateTimeOffset time)
    {
        if (time >= _ctx.Watermark)
        {
            return Lateness.OnTime;
        }

        return time >= _ctx.Watermark - _settings.AllowedLateness ? Lateness.Late : Lateness.TooLate;
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

        // Step 5: watermark-driven work (close panes, fire timers, resolve, publish partials).
        if (advanced)
        {
            foreach (var evaluator in _evaluators.Values)
            {
                evaluator.OnWatermark(_ctx.Watermark, _ctx);
            }
        }

        return _ctx.Output.ToArray();
    }

    /// <summary>
    /// An event older than the watermark minus allowed lateness (SPEC §6.1 step 3). Its windows are closed, so window and zone rules
    /// skip it. Absence rules still apply it: a late pickup can only disarm a timer or resolve an alert, which is exactly what
    /// SPEC §5.4 asks for ("a picked_up after the timer fired, including a late one, emits resolved"). The watermark does not move.
    /// </summary>
    public IReadOnlyList<AlertCandidate> ProcessTooLate(OrderEvent evt, DateTimeOffset time, RecordInfo record)
    {
        _ctx.Clear();
        _ctx.Record = record;
        foreach (var evaluator in _evaluators.Values.OfType<AbsenceEvaluator>())
        {
            evaluator.OnEvent(evt, time, _ctx);
        }

        return _ctx.Output.ToArray();
    }

    /// <summary>The whole engine as versioned JSON: watermark plus each evaluator's state, tagged with its rule version.</summary>
    public JsonObject SaveState()
    {
        var evaluators = new JsonObject();
        foreach (var (id, evaluator) in _evaluators)
        {
            evaluators[id] = new JsonObject { ["ruleVersion"] = evaluator.Rule.Version, ["state"] = evaluator.SaveState() };
        }

        return new JsonObject
        {
            ["v"] = StateVersion,
            ["watermark"] = _ctx.Watermark == DateTimeOffset.MinValue ? null : JsonValue.Create(_ctx.Watermark),
            ["evaluators"] = evaluators,
        };
    }

    /// <summary>
    /// Restore a <see cref="SaveState"/> snapshot. Call after <see cref="SetRules"/>. State saved under another rule version is
    /// discarded on purpose: a changed rule starts fresh (SPEC §5).
    /// </summary>
    /// <returns>The ids of the rules whose state was restored.</returns>
    public IReadOnlyList<string> LoadState(JsonObject state)
    {
        if (state["v"]?.GetValue<int>() != StateVersion)
        {
            throw new InvalidOperationException($"unsupported engine state version {state["v"]}");
        }

        _ctx.Watermark = state["watermark"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.MinValue;
        var restored = new List<string>();
        foreach (var (id, node) in state["evaluators"]?.AsObject() ?? [])
        {
            if (node is null || !_evaluators.TryGetValue(id, out var evaluator) || node["ruleVersion"]?.GetValue<int>() != evaluator.Rule.Version)
            {
                continue;
            }

            evaluator.LoadState(node["state"]!);
            restored.Add(id);
        }

        return restored;
    }
}
