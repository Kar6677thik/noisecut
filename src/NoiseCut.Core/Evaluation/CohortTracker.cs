using NoiseCut.Core.Events;
using NoiseCut.Core.Rules;
using NoiseCut.Core.Time;

namespace NoiseCut.Core.Evaluation;

/// <summary>
/// Cohort attribution for ratio rules with <c>cohortBy</c> (M3). Remembers, per order, the hour of its denominator event
/// (when it was placed), so its numerator event (cancellation) counts towards <i>that</i> hour, not the hour the cancellation
/// happened to arrive in.
///
/// Why it matters: cancellations trail placements by up to ~35 minutes. Counting them by arrival time compares two different
/// populations; when demand drops (the daily curve halves at 15:00), the trailing cancellations of the busier period inflate the
/// rate up to 2× with no real change in behaviour. Cohorts are also exactly how the seeded baselines are defined.
///
/// Because a cohort keeps receiving cancellations after its hour ends, cohort hours become final <see cref="Grace"/> later.
/// </summary>
internal sealed class CohortTracker
{
    /// <summary>How long after hour end a cohort may still receive numerator events (longest placed→cancelled path is ~35 min).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(45);

    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Keep = Hour + Grace + TimeSpan.FromMinutes(30);

    private readonly string _field;
    private readonly Dictionary<string, DateTimeOffset> _hourOf = new(StringComparer.Ordinal);
    private readonly PriorityQueue<string, DateTimeOffset> _expiry = new();

    private CohortTracker(string field) => _field = field;

    public static CohortTracker? For(Rule rule) => rule.Definition.CohortBy is { } field ? new CohortTracker(field) : null;

    /// <summary>A denominator event: its cohort is the hour it happened in.</summary>
    public void Remember(OrderEvent evt, DateTimeOffset hour)
    {
        if (EventFields.GetString(evt, _field) is { } id)
        {
            _hourOf[id] = hour;
            _expiry.Enqueue(id, hour);
        }
    }

    /// <summary>A numerator event: the hour of its cohort, or its own hour if we never saw the denominator (e.g. before a restart).</summary>
    public DateTimeOffset HourFor(OrderEvent evt, DateTimeOffset ownHour) =>
        EventFields.GetString(evt, _field) is { } id && _hourOf.Remove(id, out var hour) ? hour : ownHour;

    public void Expire(DateTimeOffset watermark)
    {
        while (_expiry.TryPeek(out var id, out var hour) && hour + Keep < watermark)
        {
            _expiry.Dequeue();
            if (_hourOf.TryGetValue(id, out var current) && current == hour)
            {
                _hourOf.Remove(id);
            }
        }
    }

    public List<CohortEntry> Save() => _hourOf.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new CohortEntry(e.Key, e.Value)).ToList();

    public void Load(IEnumerable<CohortEntry>? entries)
    {
        _hourOf.Clear();
        _expiry.Clear();
        foreach (var e in entries ?? [])
        {
            _hourOf[e.Id] = e.Hour;
            _expiry.Enqueue(e.Id, e.Hour);
        }
    }

    /// <summary>When a zone-hour can no longer change: hour end, plus the cohort grace if any, plus allowed lateness.</summary>
    public static TimeSpan FinalAfter(Rule rule, TimeSpan allowedLateness) =>
        Hour + allowedLateness + (rule.Definition.CohortBy is null ? TimeSpan.Zero : Grace);

    /// <summary>The hour bucket for an event time.</summary>
    public static DateTimeOffset HourOf(DateTimeOffset time) => Panes.Floor(time, Hour);
}

internal sealed record CohortEntry(string Id, DateTimeOffset Hour);
