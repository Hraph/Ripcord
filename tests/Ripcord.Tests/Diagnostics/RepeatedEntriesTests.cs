using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Diagnostics;

/// The same failure every fifteen seconds is written once an hour, with what was held back.
public class RepeatedEntriesTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    private static readonly DiagnosticEntry Failure =
        DiagnosticEntry.Of("hyper-v", "the local Hyper-V state could not be read", "System.Exception: boom");

    [Fact]
    public void The_first_is_written_the_repeats_within_the_hour_are_not()
    {
        RepeatedEntries repeated = new(TimeSpan.FromHours(1));

        Assert.Equal([Failure], repeated.Admit(Failure, Start));
        Assert.Empty(repeated.Admit(Failure, Start.AddSeconds(15)));
        Assert.Empty(repeated.Admit(Failure, Start.AddMinutes(59)));
    }

    [Fact]
    public void After_the_hour_it_is_written_again_after_how_many_were_held_back()
    {
        RepeatedEntries repeated = new(TimeSpan.FromHours(1));
        repeated.Admit(Failure, Start);
        repeated.Admit(Failure, Start.AddSeconds(15));
        repeated.Admit(Failure, Start.AddSeconds(30));

        IReadOnlyList<DiagnosticEntry> written = repeated.Admit(Failure, Start.AddHours(1));

        Assert.Equal(2, written.Count);
        Assert.Equal("the next line came 2 more time(s) since 08:00:00 UTC", written[0].Message);
        Assert.Equal(Failure, written[1]);
    }

    [Fact]
    public void A_different_line_is_never_held_back()
    {
        RepeatedEntries repeated = new(TimeSpan.FromHours(1));
        repeated.Admit(Failure, Start);

        DiagnosticEntry other = DiagnosticEntry.Of("snapshot", "this host could not publish its snapshot");

        Assert.Equal([other], repeated.Admit(other, Start.AddSeconds(15)));
    }

    /// A message carrying a changing value must not grow the table without end.
    [Fact]
    public void The_table_never_grows_past_its_cap()
    {
        RepeatedEntries repeated = new(TimeSpan.FromHours(1));

        for (int index = 0; index < RepeatedEntries.MaxKinds * 3; index++)
        {
            repeated.Admit(DiagnosticEntry.Of("x", $"value {index}"), Start.AddSeconds(index));
        }

        Assert.Equal(
            [DiagnosticEntry.Of("x", "value 0")],
            repeated.Admit(DiagnosticEntry.Of("x", "value 0"), Start.AddSeconds(RepeatedEntries.MaxKinds * 3)));
    }

    /// The publishing journal's own lines are summaries already: never held back.
    [Fact]
    public void An_exempt_operation_is_always_written()
    {
        RepeatedEntries repeated = new(TimeSpan.FromHours(1), ["publish"]);
        DiagnosticEntry line = DiagnosticEntry.Of("publish", "not published: WMI is down");

        Assert.Equal([line], repeated.Admit(line, Start));
        Assert.Equal([line], repeated.Admit(line, Start.AddSeconds(15)));
    }

    /// A failure that stopped coming back still says how often it came, the next time anything
    /// is written — it used to vanish with its count.
    [Fact]
    public void A_kind_that_stopped_says_what_was_held_back_when_it_is_dropped()
    {
        RepeatedEntries repeated = new(TimeSpan.FromHours(1));
        repeated.Admit(Failure, Start);
        repeated.Admit(Failure, Start.AddSeconds(15));
        repeated.Admit(Failure, Start.AddSeconds(30));

        DiagnosticEntry other = DiagnosticEntry.Of("publish", "published again");
        IReadOnlyList<DiagnosticEntry> written = repeated.Admit(other, Start.AddHours(2));

        Assert.Equal(2, written.Count);
        Assert.Equal("hyper-v", written[0].Operation);
        Assert.Contains("came 2 more time(s)", written[0].Message, StringComparison.Ordinal);
        Assert.Equal(other, written[1]);
    }

    /// Evicted at the cap with repeats held back: said, not lost.
    [Fact]
    public void A_kind_evicted_at_the_cap_says_what_was_held_back()
    {
        RepeatedEntries repeated = new(TimeSpan.FromHours(1));
        repeated.Admit(Failure, Start);
        repeated.Admit(Failure, Start.AddSeconds(1));

        IReadOnlyList<DiagnosticEntry> last = [];

        for (int index = 0; index < RepeatedEntries.MaxKinds; index++)
        {
            last = repeated.Admit(DiagnosticEntry.Of("wmi", $"line {index}"), Start.AddSeconds(2 + index));
        }

        Assert.Contains(last, entry => entry.Message.Contains("came 1 more time(s)", StringComparison.Ordinal));
    }
}
