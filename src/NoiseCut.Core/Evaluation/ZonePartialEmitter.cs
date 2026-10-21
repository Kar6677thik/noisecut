using System.Text.Json.Nodes;
using NoiseCut.Core.Domain;
using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Time;

namespace NoiseCut.Core.Evaluation;

/// <summary>
/// The order-stage half of a zone rule (SPEC §6.4, ADR-0004). It does not decide anything: it counts this partition's
/// numerator/denominator events per (zone, hour) and, every time the watermark closes a pane, publishes them as
/// <see cref="ZonePartial"/>s for the zone stage to merge.
///
/// What gets published at each pane close:
/// <list type="bullet">
/// <item>the current hour for <b>every</b> zone, zero counts included, so the zone stage learns this partition's watermark
///   even for zones it has no orders for (otherwise one quiet partition would freeze the zone stage, like ADR-0012's idle-partition problem);</item>
/// <item>any other hour whose counts changed since the last publish (late events, or cancellations of an earlier cohort).</item>
/// </list>
/// </summary>
internal sealed class ZonePartialEmitter : IRuleEvaluator
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly int _partition;
    private readonly EventSelector _numerator;
    private readonly EventSelector _denominator;
    private readonly SortedDictionary<(string Zone, DateTimeOffset Hour), Tally> _cells = new(CellOrder.Instance);
    private readonly HashSet<(string Zone, DateTimeOffset Hour)> _dirty = [];
    private readonly CohortTracker? _cohort;
    private DateTimeOffset? _lastClosed;
    private long _seq;

    public ZonePartialEmitter(Rule rule, int partition)
    {
        Rule = rule;
        _partition = partition;
        _numerator = rule.Definition.Numerator ?? throw new ArgumentException("ratio needs a numerator", nameof(rule));
        _denominator = rule.Definition.Denominator ?? throw new ArgumentException("ratio needs a denominator", nameof(rule));
        _cohort = CohortTracker.For(rule);
    }

    public Rule Rule { get; }

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
            hour = _cohort.HourFor(evt, hour); // a cancellation counts towards the hour its order was placed in
        }

        var key = (zone, hour);
        if (!_cells.TryGetValue(key, out var cell))
        {
            cell = new Tally();
            _cells[key] = cell;
        }

        cell.Numerator += isNumerator ? 1 : 0;
        cell.Denominator += isDenominator ? 1 : 0;
        _dirty.Add(key);
    }

    public void OnWatermark(DateTimeOffset watermark, EvaluationContext ctx)
    {
        var closed = Panes.LastClosed(watermark);
        if (_lastClosed is { } last && closed <= last)
        {
            return;
        }

        _lastClosed = closed;
        var currentHour = Panes.Floor(closed, Hour);
        var toPublish = new SortedSet<(string Zone, DateTimeOffset Hour)>(_dirty, CellOrder.Instance);
        foreach (var zone in Topology.Zones)
        {
            toPublish.Add((zone.Id, currentHour));
        }

        foreach (var key in toPublish)
        {
            var counts = _cells.GetValueOrDefault(key);
            ctx.EmitPartial(new ZonePartial
            {
                RuleId = Rule.Id,
                RuleVersion = Rule.Version,
                ZoneId = key.Zone,
                HourStart = key.Hour,
                SourcePartition = _partition,
                Numerator = counts?.Numerator ?? 0,
                Denominator = counts?.Denominator ?? 0,
                SourceWatermark = watermark,
                Seq = ++_seq,
                TriggerProducedAt = ctx.Record.ProducedAt,
            });
        }

        _dirty.Clear();

        // An hour can no longer change once the watermark is past its end + allowed lateness (later events are dropped as too late).
        var finalAfter = CohortTracker.FinalAfter(Rule, ctx.Settings.AllowedLateness);
        foreach (var key in _cells.Keys.Where(k => k.Hour + finalAfter + Panes.Size < watermark).ToList())
        {
            _cells.Remove(key);
        }

        _cohort?.Expire(watermark);
    }

    public JsonNode SaveState() => StateJson.To(new State(
        _lastClosed,
        _seq,
        _cells.Select(c => new CellDto(c.Key.Zone, c.Key.Hour, c.Value.Numerator, c.Value.Denominator, _dirty.Contains(c.Key))).ToList(),
        _cohort?.Save()));

    public void LoadState(JsonNode state)
    {
        var s = StateJson.From<State>(state);
        _lastClosed = s.LastClosed;
        _seq = s.Seq;
        _cohort?.Load(s.Cohort);
        _cells.Clear();
        _dirty.Clear();
        foreach (var c in s.Cells)
        {
            _cells[(c.Zone, c.Hour)] = new Tally { Numerator = c.Numerator, Denominator = c.Denominator };
            if (c.Dirty)
            {
                _dirty.Add((c.Zone, c.Hour));
            }
        }
    }

    private sealed record State(DateTimeOffset? LastClosed, long Seq, List<CellDto> Cells, List<CohortEntry>? Cohort);

    private sealed record CellDto(string Zone, DateTimeOffset Hour, long Numerator, long Denominator, bool Dirty);

    private sealed class Tally
    {
        public long Numerator { get; set; }

        public long Denominator { get; set; }
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
