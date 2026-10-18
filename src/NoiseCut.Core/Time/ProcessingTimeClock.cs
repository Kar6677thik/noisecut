namespace NoiseCut.Core.Time;

/// <summary>
/// Naive time assignment (Engine:TimeMode=ProcessingTime, SPEC §6.2).
/// The engine ignores <c>occurredAt</c> and stamps each record with "now", translated into sim-clock units:
/// <c>assigned = anchorSim + (wallNow − anchorWall) × TimeScale</c>, anchored on the first record it sees.
/// This is deliberately wrong in a way that matters: after a restart, a backlog of several minutes of events is read in
/// a few wall seconds, so it all lands in the same 1-minute pane (Break #2).
/// </summary>
public sealed class ProcessingTimeClock(TimeProvider wall, double timeScale)
{
    private DateTimeOffset? _anchorSim;
    private DateTimeOffset _anchorWall;

    public DateTimeOffset Assign(DateTimeOffset occurredAt)
    {
        var now = wall.GetUtcNow();
        if (_anchorSim is null)
        {
            _anchorSim = occurredAt;
            _anchorWall = now;
        }

        var elapsed = TimeSpan.FromTicks((long)((now - _anchorWall).Ticks * timeScale));
        return _anchorSim.Value + elapsed;
    }
}
