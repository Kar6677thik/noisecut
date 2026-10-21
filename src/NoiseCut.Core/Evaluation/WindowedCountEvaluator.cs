using System.Text.Json.Nodes;
using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Time;

namespace NoiseCut.Core.Evaluation;

/// <summary>
/// Rule kind <c>windowed_count</c> (SPEC §5.2): "at least N distinct things matched within a sliding window".
///
/// State per entity is a set of 1-minute panes. The window that ends at pane <c>p</c> is the union of panes
/// <c>p − size + 1 … p</c>. Two asymmetric decisions:
/// <list type="bullet">
/// <item><b>Firing is early.</b> A count can only grow, so the moment it reaches the threshold the condition is definitely true.</item>
/// <item><b>Resolving waits for the watermark.</b> A count is only known to be <i>low</i> once its window can no longer change.</item>
/// </list>
/// </summary>
internal sealed class WindowedCountEvaluator : IRuleEvaluator
{
    private const int MaxSamples = 5;

    private readonly EventSelector _source;
    private readonly TimeSpan _size;
    private readonly ConditionSpec _condition;
    private readonly Threshold _threshold;
    private readonly SortedDictionary<string, EntityState> _entities = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastClosed;

    public WindowedCountEvaluator(Rule rule)
    {
        Rule = rule;
        var def = rule.Definition;
        _source = def.Source ?? throw new ArgumentException("windowed_count needs a source", nameof(rule));
        _size = def.Window?.Size ?? throw new ArgumentException("windowed_count needs a window", nameof(rule));
        _condition = def.Condition ?? throw new ArgumentException("windowed_count needs a condition", nameof(rule));
        _threshold = new Threshold(RuleEvaluators.OpName(_condition.Op), _condition.Value);
    }

    public Rule Rule { get; }

    /// <summary>Number of entities currently holding state (for tests and metrics).</summary>
    internal int EntityCount => _entities.Count;

    public void OnEvent(OrderEvent evt, DateTimeOffset time, EvaluationContext ctx)
    {
        if (!RuleEvaluators.Selects(_source, evt) || EventFields.GetString(evt, Rule.Definition.Entity) is not { } entityKey)
        {
            return;
        }

        // distinctBy=orderId: an order that slips three times still counts as one delayed order.
        var distinctKey = Rule.Definition.DistinctBy is { } field
            ? EventFields.GetString(evt, field) ?? evt.EventId.ToString()
            : evt.EventId.ToString();

        if (!_entities.TryGetValue(entityKey, out var state))
        {
            state = new EntityState(RuleEvaluators.GroupKeyOf(Rule, evt, entityKey));
            _entities[entityKey] = state;
        }

        var pane = Panes.Floor(time);
        if (!state.Panes.TryGetValue(pane, out var members))
        {
            members = new Dictionary<string, Guid>(StringComparer.Ordinal);
            state.Panes[pane] = members;
        }

        members.TryAdd(distinctKey, evt.EventId);

        var (count, samples) = CountWindowEndingAt(state, pane);
        if (!Holds(count))
        {
            return;
        }

        if (state.CrossedPane is null)
        {
            // false → true: a new episode starts.
            state.CrossedPane = pane;
            EmitFiring(entityKey, state, pane, count, samples, ctx);
        }
        else if (ctx.Settings.EmitMode == EmitMode.EveryMatch)
        {
            // Naive: shout again on every matching event while the condition stays true (Break #3).
            EmitFiring(entityKey, state, pane, count, samples, ctx);
        }
    }

    public void OnWatermark(DateTimeOffset watermark, EvaluationContext ctx)
    {
        var closed = Panes.LastClosed(watermark);
        if (_lastClosed is { } last && closed <= last)
        {
            return;
        }

        var from = _lastClosed is { } previous ? previous + Panes.Size : closed;
        _lastClosed = closed;

        foreach (var (entityKey, state) in _entities)
        {
            if (state.CrossedPane is not null)
            {
                CheckResolution(entityKey, state, from, closed, ctx);
            }
        }

        CollectGarbage(closed, ctx.Settings.AllowedLateness);
    }

    private void CheckResolution(string entityKey, EntityState state, DateTimeOffset from, DateTimeOffset to, EvaluationContext ctx)
    {
        // A window ending before the pane that crossed the threshold says nothing about this episode.
        var start = from > state.CrossedPane!.Value ? from : state.CrossedPane.Value;

        // Past (last data pane + window size) every window is empty, so checking further is pointless.
        var lastData = state.Panes.Count > 0 ? state.Panes.Keys.Max() : start;
        var end = to < lastData + _size ? to : lastData + _size;

        for (var q = start; q <= end; q += Panes.Size)
        {
            var (count, _) = CountWindowEndingAt(state, q);
            if (Holds(count))
            {
                continue;
            }

            // true → false, and the window is closed, so this is final.
            var episodeStart = EpisodeStartFor(state.CrossedPane.Value);
            state.CrossedPane = null;
            ctx.Emit(
                Rule,
                CandidateStatus.Resolved,
                entityKey,
                state.GroupKey,
                episodeStart,
                _threshold,
                new Dictionary<string, double> { ["count"] = count },
                WindowStartFor(q),
                q + Panes.Size);

            // An early-fired window further ahead may already be over the threshold again: that is a new episode.
            foreach (var pane in state.Panes.Keys.Where(p => p > q))
            {
                var (ahead, samples) = CountWindowEndingAt(state, pane);
                if (Holds(ahead))
                {
                    state.CrossedPane = pane;
                    EmitFiring(entityKey, state, pane, ahead, samples, ctx);
                    break;
                }
            }

            return;
        }
    }

    private void EmitFiring(string entityKey, EntityState state, DateTimeOffset pane, int count, IReadOnlyList<Guid> samples, EvaluationContext ctx) =>
        ctx.Emit(
            Rule,
            CandidateStatus.Firing,
            entityKey,
            state.GroupKey,
            EpisodeStartFor(state.CrossedPane!.Value),
            _threshold,
            new Dictionary<string, double> { ["count"] = count },
            WindowStartFor(pane),
            pane + Panes.Size,
            samples);

    private (int Count, IReadOnlyList<Guid> Samples) CountWindowEndingAt(EntityState state, DateTimeOffset endPane)
    {
        var windowStart = WindowStartFor(endPane);
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var samples = new List<Guid>(MaxSamples);
        foreach (var (pane, members) in state.Panes)
        {
            if (pane < windowStart || pane > endPane)
            {
                continue;
            }

            foreach (var (key, eventId) in members)
            {
                if (distinct.Add(key) && samples.Count < MaxSamples)
                {
                    samples.Add(eventId);
                }
            }
        }

        return (distinct.Count, samples);
    }

    private void CollectGarbage(DateTimeOffset closed, TimeSpan allowedLateness)
    {
        // The oldest window still able to change ends at closed + 1 pane; anything before its first pane is dead,
        // except that a late event (up to allowedLateness behind) may still land in an older pane.
        var keepFrom = WindowStartFor(closed + Panes.Size) - allowedLateness;
        var emptyEntities = new List<string>();
        foreach (var (entityKey, state) in _entities)
        {
            foreach (var pane in state.Panes.Keys.Where(p => p < keepFrom).ToList())
            {
                state.Panes.Remove(pane);
            }

            if (state.Panes.Count == 0 && state.CrossedPane is null)
            {
                emptyEntities.Add(entityKey);
            }
        }

        foreach (var entityKey in emptyEntities)
        {
            _entities.Remove(entityKey);
        }
    }

    public JsonNode SaveState() => StateJson.To(new State(
        _lastClosed,
        _entities.Select(e => new EntityDto(
            e.Key,
            e.Value.GroupKey,
            e.Value.CrossedPane,
            e.Value.Panes.Select(p => new PaneDto(p.Key, new Dictionary<string, Guid>(p.Value, StringComparer.Ordinal))).ToList())).ToList()));

    public void LoadState(JsonNode state)
    {
        var s = StateJson.From<State>(state);
        _lastClosed = s.LastClosed;
        _entities.Clear();
        foreach (var e in s.Entities)
        {
            var entity = new EntityState(e.GroupKey) { CrossedPane = e.CrossedPane };
            foreach (var p in e.Panes)
            {
                entity.Panes[p.Pane] = new Dictionary<string, Guid>(p.Members, StringComparer.Ordinal);
            }

            _entities[e.Key] = entity;
        }
    }

    private bool Holds(int count) => _condition.Op == ConditionOp.Gt ? count > _condition.Value : count >= _condition.Value;

    private DateTimeOffset WindowStartFor(DateTimeOffset endPane) => endPane - _size + Panes.Size;

    // The episode starts at the first pane of the window that first crossed the threshold (SPEC §5.2).
    private DateTimeOffset EpisodeStartFor(DateTimeOffset crossedPane) => WindowStartFor(crossedPane);

    private sealed record State(DateTimeOffset? LastClosed, List<EntityDto> Entities);

    private sealed record EntityDto(string Key, string GroupKey, DateTimeOffset? CrossedPane, List<PaneDto> Panes);

    private sealed record PaneDto(DateTimeOffset Pane, Dictionary<string, Guid> Members);

    private sealed class EntityState(string groupKey)
    {
        public string GroupKey { get; } = groupKey;

        public SortedDictionary<DateTimeOffset, Dictionary<string, Guid>> Panes { get; } = [];

        /// <summary>The pane whose window first crossed the threshold in the open episode; null when no episode is open.</summary>
        public DateTimeOffset? CrossedPane { get; set; }
    }
}
