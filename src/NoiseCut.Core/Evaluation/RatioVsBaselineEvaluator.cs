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
/// <b>M1 limitation:</b> with events keyed by restaurant, one zone's orders are spread over several partitions.
/// Counting locally is only correct while a single engine owns every partition. M3 moves this into the zone stage (ADR-0004).
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
    }

    public Rule Rule { get; }

    public void OnEvent(OrderEvent evt, DateTimeOffset time, EvaluationContext ctx)
    {
        var isNumerator = RuleEvaluators.Selects(_numerator, evt);
        var isDenominator = RuleEvaluators.Selects(_denominator, evt);
        if ((!isNumerator && !isDenominator) || EventFields.GetString(evt, Rule.Definition.Entity) is not { } zone)
        {
            return;
        }

        var key = (zone, Panes.Floor(time, Hour));
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

            var hourClosed = hour + Hour <= closed + Panes.Size;
            if (hourClosed)
            {
                if (cell.Fired)
                {
                    ctx.Emit(Rule, CandidateStatus.Resolved, zone, zone, hour, _threshold, Observed(cell, cell.LastBaseline), hour, hour + Hour);
                }

                finished.Add((zone, hour));
            }
        }

        foreach (var key in finished)
        {
            _cells.Remove(key);
        }
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
