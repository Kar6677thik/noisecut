using System.Text.Json.Nodes;
using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;

namespace NoiseCut.Core.Evaluation;

/// <summary>
/// Rule kind <c>absence</c> (SPEC §5.4): "X happened, and Y did not follow within T".
///
/// A trigger (food_ready) arms a timer at <c>time + timeout</c>. The timer fires when the <b>watermark</b> passes the deadline,
/// never on the wall clock, so a paused consumer can't produce a burst of fake timeouts when it wakes up (in event-time mode).
/// The expected event (picked_up) or a cancelling event disarms it, or resolves the alert if it already fired.
///
/// <b>Order-insensitive mode</b> (robust, <see cref="EvaluationSettings.OrderInsensitiveAbsence"/>): if picked_up arrives
/// <i>before</i> food_ready (out-of-order delivery), the order is remembered as completed, so the late food_ready never arms a
/// timer. Without it (naive), the early pickup is forgotten and a false alert follows (Break #2).
/// </summary>
internal sealed class AbsenceEvaluator : IRuleEvaluator
{
    private readonly EventSelector _trigger;
    private readonly EventSelector _expected;
    private readonly HashSet<string> _cancelIf;
    private readonly TimeSpan _timeout;
    private readonly Threshold _threshold;
    private readonly Dictionary<string, Pending> _orders = new(StringComparer.Ordinal);

    // Orders whose expected/cancelling event arrived before any trigger, with the time after which we forget them.
    private readonly Dictionary<string, DateTimeOffset> _completed = new(StringComparer.Ordinal);

    // Deadlines ordered soonest first. Entries are never removed eagerly: when one surfaces we check it is still current.
    private readonly PriorityQueue<string, DateTimeOffset> _deadlines = new();
    private readonly PriorityQueue<string, DateTimeOffset> _expiries = new();
    private readonly PriorityQueue<string, DateTimeOffset> _completedExpiries = new();
    private bool _rebuildExpiries;

    public AbsenceEvaluator(Rule rule)
    {
        Rule = rule;
        var def = rule.Definition;
        _trigger = def.Trigger ?? throw new ArgumentException("absence needs a trigger", nameof(rule));
        _expected = def.Expected ?? throw new ArgumentException("absence needs an expected event", nameof(rule));
        _cancelIf = new HashSet<string>(def.CancelIf ?? [], StringComparer.Ordinal);
        _timeout = def.Timeout ?? throw new ArgumentException("absence needs a timeout", nameof(rule));
        _threshold = new Threshold("gte", _timeout.TotalMinutes);
    }

    public Rule Rule { get; }

    internal int PendingCount => _orders.Count;

    public void OnEvent(OrderEvent evt, DateTimeOffset time, EvaluationContext ctx)
    {
        if (EventFields.GetString(evt, Rule.Definition.CorrelateBy ?? "orderId") is not { } orderId)
        {
            return;
        }

        if (RuleEvaluators.Selects(_trigger, evt))
        {
            if (_completed.Remove(orderId))
            {
                return; // the pickup already happened; it just arrived first
            }

            if (!_orders.ContainsKey(orderId))
            {
                var deadline = time + _timeout;
                _orders[orderId] = new Pending(time, deadline, RuleEvaluators.GroupKeyOf(Rule, evt, orderId));
                _deadlines.Enqueue(orderId, deadline);
            }

            return;
        }

        if (!RuleEvaluators.Selects(_expected, evt) && !_cancelIf.Contains(evt.Type))
        {
            return;
        }

        if (_orders.Remove(orderId, out var pending))
        {
            if (pending.Fired)
            {
                // The pickup (or cancellation) finally arrived after we alerted: close the episode.
                ctx.Emit(
                    Rule,
                    CandidateStatus.Resolved,
                    orderId,
                    pending.GroupKey,
                    pending.Deadline,
                    _threshold,
                    new Dictionary<string, double> { ["waitedMinutes"] = Math.Round((time - pending.ReadyAt).TotalMinutes, 1) });
            }

            return;
        }

        if (ctx.Settings.OrderInsensitiveAbsence)
        {
            // Remember it long enough for an out-of-order trigger to show up (it can be at most this far behind).
            var forgetAt = time + _timeout + ctx.Settings.AllowedLateness + ctx.Settings.MaxOutOfOrderness;
            _completed[orderId] = forgetAt;
            _completedExpiries.Enqueue(orderId, forgetAt);
        }

        // Naive: an expected event with no armed timer is simply dropped here.
    }

    public void OnWatermark(DateTimeOffset watermark, EvaluationContext ctx)
    {
        if (_rebuildExpiries)
        {
            // After a restore: expiry times are derived (deadline + MaxOrderAge), so they are re-queued here, where settings are known.
            foreach (var (id, p) in _orders.Where(o => o.Value.Fired))
            {
                _expiries.Enqueue(id, p.Deadline + ctx.Settings.MaxOrderAge);
            }

            _rebuildExpiries = false;
        }

        while (_deadlines.TryPeek(out var orderId, out var deadline) && deadline <= watermark)
        {
            _deadlines.Dequeue();
            if (!_orders.TryGetValue(orderId, out var pending) || pending.Fired || pending.Deadline != deadline)
            {
                continue; // stale queue entry
            }

            pending.Fired = true;
            _expiries.Enqueue(orderId, deadline + ctx.Settings.MaxOrderAge);
            ctx.Emit(
                Rule,
                CandidateStatus.Firing,
                orderId,
                pending.GroupKey,
                pending.Deadline,
                _threshold,
                new Dictionary<string, double> { ["waitedMinutes"] = Math.Round((watermark - pending.ReadyAt).TotalMinutes, 1) });
        }

        // A fired alert whose pickup never comes is forgotten eventually (without a "resolved"), so memory stays bounded.
        while (_expiries.TryPeek(out var orderId, out var expiry) && expiry <= watermark)
        {
            _expiries.Dequeue();
            if (_orders.TryGetValue(orderId, out var pending) && pending.Fired && pending.Deadline + ctx.Settings.MaxOrderAge == expiry)
            {
                _orders.Remove(orderId);
            }
        }

        while (_completedExpiries.TryPeek(out var orderId, out var forgetAt) && forgetAt <= watermark)
        {
            _completedExpiries.Dequeue();
            if (_completed.TryGetValue(orderId, out var current) && current == forgetAt)
            {
                _completed.Remove(orderId);
            }
        }
    }

    public JsonNode SaveState() => StateJson.To(new State(
        _orders.OrderBy(o => o.Key, StringComparer.Ordinal)
            .Select(o => new PendingDto(o.Key, o.Value.ReadyAt, o.Value.Deadline, o.Value.GroupKey, o.Value.Fired))
            .ToList(),
        _completed.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new CompletedDto(c.Key, c.Value)).ToList()));

    public void LoadState(JsonNode state)
    {
        var s = StateJson.From<State>(state);
        _orders.Clear();
        _completed.Clear();
        _deadlines.Clear();
        _expiries.Clear();
        _completedExpiries.Clear();
        foreach (var p in s.Orders)
        {
            _orders[p.OrderId] = new Pending(p.ReadyAt, p.Deadline, p.GroupKey) { Fired = p.Fired };
            _deadlines.Enqueue(p.OrderId, p.Deadline);
        }

        foreach (var c in s.Completed)
        {
            _completed[c.OrderId] = c.ForgetAt;
            _completedExpiries.Enqueue(c.OrderId, c.ForgetAt);
        }

        _rebuildExpiries = true;
    }

    private sealed record State(List<PendingDto> Orders, List<CompletedDto> Completed);

    private sealed record PendingDto(string OrderId, DateTimeOffset ReadyAt, DateTimeOffset Deadline, string GroupKey, bool Fired);

    private sealed record CompletedDto(string OrderId, DateTimeOffset ForgetAt);

    private sealed class Pending(DateTimeOffset readyAt, DateTimeOffset deadline, string groupKey)
    {
        public DateTimeOffset ReadyAt { get; } = readyAt;

        public DateTimeOffset Deadline { get; } = deadline;

        public string GroupKey { get; } = groupKey;

        public bool Fired { get; set; }
    }
}
