using System.Text.Json.Nodes;
using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Time;

namespace NoiseCut.Core.Evaluation;

/// <summary>
/// Rule kind <c>ratio_vs_baseline</c> (SPEC §5.3): "this hour's numerator/denominator is at least k× the same hour last week".
///
/// Counts per (zone, hour). Evaluated whenever the watermark closes a pane. Guards stop tiny samples from looking like spikes:
/// nothing is evaluated until <c>minDenominator</c> events are seen, and the baseline rate is floored at <c>minBaselineRate</c>,
/// so a zero or missing baseline can't make any rate look "infinitely" high.
///
/// At most one episode per (zone, hour). It resolves when the watermark closes the hour.
///
/// Two ways to feed it:
/// <list type="bullet">
/// <item><b>Events</b> (naive, <see cref="EngineRole.Single"/>): it counts them itself. Only correct while one engine owns every partition (Break #4).</item>
/// <item><b>Totals</b> (robust, via <see cref="ZoneStageEngine"/>): the zone stage merges every partition's partial counts and calls <see cref="SetTotals"/>.</item>
/// </list>
/// An hour becomes final (resolved, and reported as a <see cref="ClosedHour"/>) only once the watermark has passed
/// hour end + allowed lateness, because a late event could still change it until then.
/// </summary>
internal sealed class RatioVsBaselineEvaluator : IRuleEvaluator
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly IBaselineSource _baselines;
    private readonly EventSelector _numerator;
    private readonly EventSelector _denominator;
    private readonly TimeSpan _baselineOffset;
    private readonly double _multiple;
    private readonly GuardsSpec _guards;
    private readonly Threshold _threshold;
    private readonly SortedDictionary<(string Zone, DateTimeOffset Hour), HourCell> _cells = new(CellOrder.Instance);
    private readonly CohortTracker? _cohort;
    private DateTimeOffset? _lastClosed;

    public RatioVsBaselineEvaluator(Rule rule, IBaselineSource baselines)
    {
        Rule = rule;
        _baselines = baselines;
        var def = rule.Definition;
        _numerator = def.Numerator ?? throw new ArgumentException("ratio needs a numerator", nameof(rule));
        _denominator = def.Denominator ?? throw new ArgumentException("ratio needs a denominator", nameof(rule));
        _baselineOffset = def.Baseline?.Offset ?? throw new ArgumentException("ratio needs a baseline", nameof(rule));
        _multiple = def.Condition?.Value ?? throw new ArgumentException("ratio needs a condition", nameof(rule));
        _guards = def.Guards ?? new GuardsSpec();
        _threshold = new Threshold("gte_multiple", _multiple);
        _cohort = CohortTracker.For(rule);
    }

    public Rule Rule { get; }

    /// <summary>The counts this instance holds right now. Exposed as a metric so Break #4 can show each engine's partial view.</summary>
    internal IEnumerable<ZoneHourCount> Counts() =>
        _cells.Select(c => new ZoneHourCount(c.Key.Zone, c.Key.Hour, c.Value.Numerator, c.Value.Denominator));

    public void OnEvent(OrderEvent evt, DateTimeOffset time, EvaluationContext ctx)
    {
        var isNumerator = RuleEvaluators.Selects(_numerator, evt);
        var isDenominator = RuleEvaluators.Selects(_denominator, evt);
        if ((!isNumerator && !isDenominator) || EventFields.GetString(evt, Rule.Definition.Entity) is not { } zone)
        {
            return;
        }

        var hour = Panes.Floor(time, Hour);
        if (isDenominator)
        {
            _cohort?.Remember(evt, hour);
        }
        else if (_cohort is not null)
        {
            hour = _cohort.HourFor(evt, hour);
        }

        var key = (zone, hour);
        if (!_cells.TryGetValue(key, out var cell))
        {
            cell = new HourCell();
            _cells[key] = cell;
        }

        if (isNumerator)
        {
            cell.Numerator++;
        }

        if (isDenominator)
        {
            cell.Denominator++;
        }
    }

    /// <summary>Zone stage: replace the counts for a zone-hour with merged totals from every source partition.</summary>
    internal void SetTotals(string zone, DateTimeOffset hour, long numerator, long denominator)
    {
        var key = (zone, hour);
        if (!_cells.TryGetValue(key, out var cell))
        {
            cell = new HourCell();
            _cells[key] = cell;
        }

        cell.Numerator = numerator;
        cell.Denominator = denominator;
    }

    public void OnWatermark(DateTimeOffset watermark, EvaluationContext ctx)
    {
        var closed = Panes.LastClosed(watermark);
        if (_lastClosed is { } last && closed <= last)
        {
            return;
        }

        _lastClosed = closed;
        var finished = new List<(string, DateTimeOffset)>();
        foreach (var ((zone, hour), cell) in _cells)
        {
            if (hour > closed)
            {
                continue; // nothing in this hour is closed yet
            }

            Evaluate(zone, hour, cell, ctx);

            var hourClosed = hour + CohortTracker.FinalAfter(Rule, ctx.Settings.AllowedLateness) <= closed + Panes.Size;
            if (hourClosed)
            {
                if (cell.Fired)
                {
                    ctx.Emit(Rule, CandidateStatus.Resolved, zone, zone, hour, _threshold, Observed(cell, cell.LastBaseline), hour, hour + Hour);
                }

                ctx.EmitClosedHour(new ClosedHour(Rule.Id, zone, hour, cell.Numerator, cell.Denominator));
                finished.Add((zone, hour));
            }
        }

        foreach (var key in finished)
        {
            _cells.Remove(key);
        }

        _cohort?.Expire(watermark);
    }

    private void Evaluate(string zone, DateTimeOffset hour, HourCell cell, EvaluationContext ctx)
    {
        if (cell.Denominator < _guards.MinDenominator)
        {
            return;
        }

        var baseline = Math.Max(_baselines.GetRate(zone, hour - _baselineOffset) ?? 0, _guards.MinBaselineRate);
        if (baseline <= 0)
        {
            return;
        }

        cell.LastBaseline = baseline;
        var rate = (double)cell.Numerator / cell.Denominator;
        if (rate < _multiple * baseline)
        {
            return;
        }

        if (!cell.Fired)
        {
            cell.Fired = true;
            ctx.Emit(Rule, CandidateStatus.Firing, zone, zone, hour, _threshold, Observed(cell, baseline), hour, hour + Hour);
        }
        else if (ctx.Settings.EmitMode == EmitMode.EveryMatch)
        {
            ctx.Emit(Rule, CandidateStatus.Firing, zone, zone, hour, _threshold, Observed(cell, baseline), hour, hour + Hour);
        }
    }

    public JsonNode SaveState() => StateJson.To(new State(
        _lastClosed,
        _cells.Select(c => new CellDto(c.Key.Zone, c.Key.Hour, c.Value.Numerator, c.Value.Denominator, c.Value.Fired, c.Value.LastBaseline)).ToList(),
        _cohort?.Save()));

    public void LoadState(JsonNode state)
    {
        var s = StateJson.From<State>(state);
        _lastClosed = s.LastClosed;
        _cohort?.Load(s.Cohort);
        _cells.Clear();
        foreach (var c in s.Cells)
        {
            _cells[(c.Zone, c.Hour)] = new HourCell { Numerator = c.Numerator, Denominator = c.Denominator, Fired = c.Fired, LastBaseline = c.LastBaseline };
        }
    }

    private sealed record State(DateTimeOffset? LastClosed, List<CellDto> Cells, List<CohortEntry>? Cohort);

    private sealed record CellDto(string Zone, DateTimeOffset Hour, long Numerator, long Denominator, bool Fired, double LastBaseline);

    private static Dictionary<string, double> Observed(HourCell cell, double baseline) => new()
    {
        ["numerator"] = cell.Numerator,
        ["denominator"] = cell.Denominator,
        ["rate"] = cell.Denominator == 0 ? 0 : Math.Round((double)cell.Numerator / cell.Denominator, 4),
        ["baselineRate"] = Math.Round(baseline, 4),
    };

    private sealed class HourCell
    {
        public long Numerator { get; set; }

        public long Denominator { get; set; }

        public bool Fired { get; set; }

        public double LastBaseline { get; set; }
    }

    private sealed class CellOrder : IComparer<(string Zone, DateTimeOffset Hour)>
    {
        public static readonly CellOrder Instance = new();

        public int Compare((string Zone, DateTimeOffset Hour) x, (string Zone, DateTimeOffset Hour) y)
        {
            var byHour = x.Hour.CompareTo(y.Hour);
            return byHour != 0 ? byHour : string.CompareOrdinal(x.Zone, y.Zone);
        }
    }
}
