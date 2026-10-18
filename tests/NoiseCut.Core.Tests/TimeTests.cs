using Microsoft.Extensions.Time.Testing;
using NoiseCut.Core.Tests.TestSupport;
using NoiseCut.Core.Time;

namespace NoiseCut.Core.Tests;

public class TimeTests
{
    [Theory]
    [InlineData("PT10M", 600)]
    [InlineData("PT30S", 30)]
    [InlineData("PT1H", 3600)]
    [InlineData("P7D", 604800)]
    public void Parses_iso_durations(string text, double seconds)
    {
        Assert.True(IsoDuration.TryParse(text, out var value));
        Assert.Equal(TimeSpan.FromSeconds(seconds), value);
    }

    [Theory]
    [InlineData("10m")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("PT")]
    public void Rejects_non_iso_durations(string? text) => Assert.False(IsoDuration.TryParse(text, out _));

    [Fact]
    public void Formats_durations_back_to_iso() => Assert.Equal("PT10M", IsoDuration.Format(TimeSpan.FromMinutes(10)));

    [Fact]
    public void Floor_puts_a_time_into_its_one_minute_pane()
    {
        var t = new DateTimeOffset(2026, 10, 17, 19, 42, 59, 999, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 19, 42, 0, TimeSpan.Zero), Panes.Floor(t));
    }

    [Fact]
    public void Floor_normalises_offsets_to_utc()
    {
        var ist = new DateTimeOffset(2026, 10, 18, 1, 12, 30, TimeSpan.FromHours(5.5));
        Assert.Equal(new DateTimeOffset(2026, 10, 17, 19, 42, 0, TimeSpan.Zero), Panes.Floor(ist));
    }

    [Fact]
    public void Last_closed_pane_ends_at_or_before_the_watermark()
    {
        Assert.Equal(Ev.At(9), Panes.LastClosed(Ev.At(10)));
        Assert.Equal(Ev.At(9), Panes.LastClosed(Ev.At(10.5)));
    }

    [Fact]
    public void Processing_time_clock_ignores_event_time_and_scales_wall_time()
    {
        var wall = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var clock = new ProcessingTimeClock(wall, timeScale: 10);

        Assert.Equal(Ev.At(0), clock.Assign(Ev.At(0)));

        wall.Advance(TimeSpan.FromSeconds(6));

        // 6 wall seconds × 10 = 1 sim minute, no matter what the event says about itself.
        Assert.Equal(Ev.At(1), clock.Assign(Ev.At(-30)));
        Assert.Equal(Ev.At(1), clock.Assign(Ev.At(+30)));
    }
}
