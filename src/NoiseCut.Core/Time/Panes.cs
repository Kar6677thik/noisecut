namespace NoiseCut.Core.Time;

/// <summary>
/// A pane is a fixed 1-minute bucket of event time. Sliding windows are sums of consecutive panes (SPEC §5.2).
/// </summary>
public static class Panes
{
    public static readonly TimeSpan Size = TimeSpan.FromMinutes(1);

    /// <summary>Start of the bucket of width <paramref name="size"/> that contains <paramref name="t"/>, in UTC.</summary>
    public static DateTimeOffset Floor(DateTimeOffset t, TimeSpan size)
    {
        var ticks = t.UtcTicks - (t.UtcTicks % size.Ticks);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    public static DateTimeOffset Floor(DateTimeOffset t) => Floor(t, Size);

    /// <summary>
    /// The latest pane that lies completely behind the watermark (its end is ≤ the watermark), so it is no longer expected to change.
    /// </summary>
    public static DateTimeOffset LastClosed(DateTimeOffset watermark) => Floor(watermark) - Size;
}
