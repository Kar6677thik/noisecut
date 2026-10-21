using System.Text.Json.Nodes;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Time;

namespace NoiseCut.Core.Evaluation;

/// <summary>
/// The zone stage (SPEC §6.4, ADR-0004): the second half of a cross-partition aggregation. It consumes
/// <see cref="ZonePartial"/>s (keyed by zone, so one zone's partials all reach the same zone-stage instance) and:
/// <list type="number">
/// <item>keeps the <b>latest partial by seq</b> for every (rule, zone, hour, source partition). Older or repeated partials
///   are no-ops, which is what makes crash replays and duplicates harmless;</item>
/// <item>sums the latest partials over source partitions, and hands the totals to an ordinary ratio evaluator;</item>
/// <item>uses the <b>minimum</b> source watermark as its own. Until every source partition has reported, nothing can be
///   final, because a silent partition might still be holding orders for this zone.</item>
/// </list>
/// One instance per <c>zone-partials</c> partition, checkpointed like the order stage.
/// </summary>
public sealed class ZoneStageEngine
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly EvaluationSettings _settings;
    private readonly IBaselineSource _baselines;
    private readonly EvaluationContext _ctx;
    private readonly int _sourcePartitions;
    private readonly SortedDictionary<string, RatioVsBaselineEvaluator> _evaluators = new(StringComparer.Ordinal);
    private readonly Dictionary<PartialKey, Latest> _latest = [];
    private readonly Dictionary<int, DateTimeOffset> _sourceWatermarks = [];

    public ZoneStageEngine(EvaluationSettings settings, IBaselineSource baselines, TimeProvider wallClock, string instance, int sourcePartitions)
    {
        _settings = settings;
        _baselines = baselines;
        _sourcePartitions = sourcePartitions;
        _ctx = new EvaluationContext(settings, wallClock, instance, "zone");
    }

    public DateTimeOffset Watermark => _ctx.Watermark;

    /// <summary>Zone-hours that became final during the last <see cref="Process"/> call (written to zone_hourly_stats as live baselines).</summary>
    public IReadOnlyList<ClosedHour> LastClosedHours => _ctx.ClosedHours.ToArray();

    public IReadOnlyList<ZoneHourCount> ZoneHourCounts() => _evaluators.Values.SelectMany(e => e.Counts()).ToList();

    public bool SetRules(IEnumerable<Rule> rules)
    {
        var wanted = rules
            .Where(r => r.Definition.Enabled && r.Definition.Kind == RuleKind.RatioVsBaseline)
            .ToDictionary(r => r.Id, StringComparer.Ordinal);
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

            _evaluators[id] = new RatioVsBaselineEvaluator(rule, _baselines);
            changed = true;
        }

        return changed;
    }

    public IReadOnlyList<AlertCandidate> Process(ZonePartial partial, RecordInfo record)
    {
        _ctx.Clear();
        _ctx.Record = record with { ProducedAt = partial.TriggerProducedAt };

        if (_sourceWatermarks.TryGetValue(partial.SourcePartition, out var known) is false || partial.SourceWatermark > known)
        {
            _sourceWatermarks[partial.SourcePartition] = partial.SourceWatermark;
        }

        if (_evaluators.TryGetValue(partial.RuleId, out var evaluator)
            && evaluator.Rule.Version == partial.RuleVersion
            && !IsFinal(evaluator.Rule, partial.HourStart))
        {
            var key = new PartialKey(partial.RuleId, partial.ZoneId, partial.HourStart, partial.SourcePartition);
            if (!_latest.TryGetValue(key, out var latest) || partial.Seq > latest.Seq)
            {
                _latest[key] = new Latest(partial.Seq, partial.Numerator, partial.Denominator);
                var totals = _latest
                    .Where(l => l.Key.RuleId == partial.RuleId && l.Key.ZoneId == partial.ZoneId && l.Key.HourStart == partial.HourStart)
                    .Aggregate((Num: 0L, Den: 0L), (sum, l) => (sum.Num + l.Value.Numerator, sum.Den + l.Value.Denominator));
                evaluator.SetTotals(partial.ZoneId, partial.HourStart, totals.Num, totals.Den);
            }
        }

        // The zone stage can only be as far along as its slowest source.
        if (_sourceWatermarks.Count >= _sourcePartitions)
        {
            var watermark = _sourceWatermarks.Values.Min();
            if (watermark > _ctx.Watermark)
            {
                _ctx.Watermark = watermark;
                foreach (var e in _evaluators.Values)
                {
                    e.OnWatermark(watermark, _ctx);
                }

                foreach (var key in _latest.Keys.Where(k => !_evaluators.TryGetValue(k.RuleId, out var e) || IsFinal(e.Rule, k.HourStart)).ToList())
                {
                    _latest.Remove(key);
                }
            }
        }

        return _ctx.Output.ToArray();
    }

    // Once the watermark is past the hour's finalization point it has been resolved and reported; later partials can't reopen it.
    private bool IsFinal(Rule rule, DateTimeOffset hour) =>
        _ctx.Watermark != DateTimeOffset.MinValue && hour + CohortTracker.FinalAfter(rule, _settings.AllowedLateness) <= Panes.LastClosed(_ctx.Watermark) + Panes.Size;

    public JsonObject SaveState()
    {
        var evaluators = new JsonObject();
        foreach (var (id, e) in _evaluators)
        {
            evaluators[id] = new JsonObject { ["ruleVersion"] = e.Rule.Version, ["state"] = e.SaveState() };
        }

        return new JsonObject
        {
            ["v"] = RuleEngine.StateVersion,
            ["watermark"] = _ctx.Watermark == DateTimeOffset.MinValue ? null : JsonValue.Create(_ctx.Watermark),
            ["sources"] = StateJson.To(_sourceWatermarks.OrderBy(s => s.Key).Select(s => new SourceDto(s.Key, s.Value)).ToList()),
            ["latest"] = StateJson.To(_latest.Select(l => new LatestDto(l.Key.RuleId, l.Key.ZoneId, l.Key.HourStart, l.Key.SourcePartition, l.Value.Seq, l.Value.Numerator, l.Value.Denominator)).ToList()),
            ["evaluators"] = evaluators,
        };
    }

    public void LoadState(JsonObject state)
    {
        if (state["v"]?.GetValue<int>() != RuleEngine.StateVersion)
        {
            throw new InvalidOperationException($"unsupported zone state version {state["v"]}");
        }

        _ctx.Watermark = state["watermark"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.MinValue;
        _sourceWatermarks.Clear();
        foreach (var s in StateJson.From<List<SourceDto>>(state["sources"]!))
        {
            _sourceWatermarks[s.Partition] = s.Watermark;
        }

        _latest.Clear();
        foreach (var l in StateJson.From<List<LatestDto>>(state["latest"]!))
        {
            _latest[new PartialKey(l.RuleId, l.ZoneId, l.HourStart, l.SourcePartition)] = new Latest(l.Seq, l.Numerator, l.Denominator);
        }

        foreach (var (id, node) in state["evaluators"]?.AsObject() ?? [])
        {
            if (node is not null && _evaluators.TryGetValue(id, out var e) && node["ruleVersion"]?.GetValue<int>() == e.Rule.Version)
            {
                e.LoadState(node["state"]!);
            }
        }
    }

    private readonly record struct PartialKey(string RuleId, string ZoneId, DateTimeOffset HourStart, int SourcePartition);

    private readonly record struct Latest(long Seq, long Numerator, long Denominator);

    private sealed record SourceDto(int Partition, DateTimeOffset Watermark);

    private sealed record LatestDto(string RuleId, string ZoneId, DateTimeOffset HourStart, int SourcePartition, long Seq, long Numerator, long Denominator);
}
