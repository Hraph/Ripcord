using Ripcord.Domain.Alerting;

namespace Ripcord.Tests.Alerting;

/// The window is written by hand in `ripcord.yaml` and read in the host's local time, so the
/// instant handed in carries the offset it is judged against.
public class QuietHoursTests
{
    private static DateTimeOffset At(int hour, int minute = 0) =>
        new(2026, 9, 13, hour, minute, 0, TimeSpan.FromHours(2));

    [Fact]
    public void A_window_crossing_midnight_covers_both_sides_of_it()
    {
        QuietHours window = Parsed("22:00-07:00");

        Assert.True(window.Covers(At(23)));
        Assert.True(window.Covers(At(3)));
        Assert.False(window.Covers(At(12)));
    }

    [Fact]
    public void A_window_inside_one_day_covers_only_that_range()
    {
        QuietHours window = Parsed("09:00-17:00");

        Assert.True(window.Covers(At(12)));
        Assert.False(window.Covers(At(3)));
        Assert.False(window.Covers(At(23)));
    }

    /// The start is in and the end is out, so two adjacent windows cannot both claim an
    /// instant and a run at exactly the end time delivers rather than holds again.
    [Fact]
    public void The_window_includes_its_start_and_excludes_its_end()
    {
        QuietHours window = Parsed("22:00-07:00");

        Assert.True(window.Covers(At(22)));
        Assert.False(window.Covers(At(7)));
    }

    /// The offset carried by the instant is what decides, not UTC: 23:00 in Paris is 21:00
    /// UTC, and an alert held until 07:00 UTC would arrive two hours late.
    [Fact]
    public void The_offset_of_the_instant_is_what_is_judged()
    {
        QuietHours window = Parsed("22:00-07:00");

        Assert.True(window.Covers(new DateTimeOffset(2026, 9, 13, 23, 0, 0, TimeSpan.FromHours(2))));
        Assert.False(window.Covers(new DateTimeOffset(2026, 9, 13, 21, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("22:00-07:00")]
    [InlineData("22:00 - 07:00")]
    [InlineData("7:30-8:00")]
    public void An_accepted_window_round_trips(string text) =>
        Assert.True(QuietHours.TryParse(text, out _));

    [Theory]
    [InlineData("")]
    [InlineData("22:00")]
    [InlineData("22:00-07:00-08:00")]
    [InlineData("25:00-07:00")]
    [InlineData("22h-7h")]
    [InlineData("22:00-22:00")]
    public void A_window_that_cannot_be_read_is_refused(string text) =>
        Assert.False(QuietHours.TryParse(text, out _));

    private static QuietHours Parsed(string text)
    {
        Assert.True(QuietHours.TryParse(text, out QuietHours? window));
        return window!;
    }
}
