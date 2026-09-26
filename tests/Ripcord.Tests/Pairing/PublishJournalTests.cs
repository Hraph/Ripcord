using Ripcord.Domain.Diagnostics;
using Ripcord.Domain.Pairing;

namespace Ripcord.Tests.Pairing;

/// What the publishing service writes: enough to tell working from stuck, never a line every
/// fifteen seconds.
public class PublishJournalTests
{
    private const string Path = @"C:\Program Files\Ripcord\state\state.json";

    private static readonly DateTimeOffset Start = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    private static Publication Ok(DateTimeOffset at, params string[] notes) =>
        new(PublicationKind.Published, Path, at, null, notes);

    private static Publication Ko(DateTimeOffset at, string reason) =>
        new(PublicationKind.NotRead, Path, at, reason, []);

    [Fact]
    public void The_first_result_is_said_and_the_healthy_ones_after_it_are_not()
    {
        (PublishJournal journal, IReadOnlyList<DiagnosticEntry> first) =
            PublishJournal.Start.After(Ok(Start), Start);

        Assert.Equal($"publishing every 15 s to {Path}", Assert.Single(first).Message);

        for (int round = 1; round < 200; round++)
        {
            DateTimeOffset at = Start.AddSeconds(15 * round);
            (journal, IReadOnlyList<DiagnosticEntry> lines) = journal.After(Ok(at), at);
            Assert.Empty(lines);
        }
    }

    [Fact]
    public void A_failure_is_said_once_then_its_recovery_with_how_long_and_how_many()
    {
        PublishJournal journal = PublishJournal.Start.After(Ok(Start), Start).Next;

        (journal, IReadOnlyList<DiagnosticEntry> failed) = journal.After(Ko(Start.AddSeconds(15), "WMI is down"), Start.AddSeconds(15));
        (journal, IReadOnlyList<DiagnosticEntry> again) = journal.After(Ko(Start.AddSeconds(30), "WMI is down"), Start.AddSeconds(30));
        (journal, IReadOnlyList<DiagnosticEntry> other) = journal.After(Ko(Start.AddSeconds(45), "access denied"), Start.AddSeconds(45));
        (_, IReadOnlyList<DiagnosticEntry> back) = journal.After(Ok(Start.AddMinutes(3)), Start.AddMinutes(3));

        Assert.Equal("not published: WMI is down", Assert.Single(failed).Message);
        Assert.Empty(again);
        Assert.Equal("not published: access denied", Assert.Single(other).Message);
        Assert.Equal("published again after 2 min, 3 attempt(s) failed", Assert.Single(back).Message);
    }

    [Fact]
    public void An_hour_healthy_is_one_line_with_the_count_and_the_last()
    {
        PublishJournal journal = PublishJournal.Start.After(Ok(Start), Start).Next;
        IReadOnlyList<DiagnosticEntry> lines = [];

        for (int round = 1; round <= 240; round++)
        {
            DateTimeOffset at = Start.AddSeconds(15 * round);
            (journal, lines) = journal.After(Ok(at), at);
        }

        Assert.Equal(
            "241 snapshot(s) published in the last hour, the last at 09:00:00 UTC",
            Assert.Single(lines).Message);
    }

    /// A failure that spans the night still leaves a line in every hour, so each day's file
    /// says what that day was.
    [Fact]
    public void An_hour_failing_is_one_line_too()
    {
        PublishJournal journal = PublishJournal.Start.After(Ko(Start, "WMI is down"), Start).Next;
        IReadOnlyList<DiagnosticEntry> lines = [];

        for (int round = 1; round <= 240; round++)
        {
            DateTimeOffset at = Start.AddSeconds(15 * round);
            (journal, lines) = journal.After(Ko(at, "WMI is down"), at);
        }

        Assert.Equal("241 attempt(s) failed in the last hour: WMI is down", Assert.Single(lines).Message);
    }

    [Fact]
    public void What_the_read_could_not_see_is_said_when_it_changes()
    {
        PublishJournal journal = PublishJournal.Start.After(Ok(Start), Start).Next;

        (journal, IReadOnlyList<DiagnosticEntry> gap) = journal.After(Ok(Start.AddSeconds(15), "BitLocker unread"), Start.AddSeconds(15));
        (journal, IReadOnlyList<DiagnosticEntry> same) = journal.After(Ok(Start.AddSeconds(30), "BitLocker unread"), Start.AddSeconds(30));
        (_, IReadOnlyList<DiagnosticEntry> full) = journal.After(Ok(Start.AddSeconds(45)), Start.AddSeconds(45));

        Assert.Equal("read with gaps: BitLocker unread", Assert.Single(gap).Message);
        Assert.Empty(same);
        Assert.Equal("read in full again", Assert.Single(full).Message);
    }

    [Fact]
    public void The_stop_accounts_for_the_time_since_the_last_summary()
    {
        PublishJournal journal = PublishJournal.Start.After(Ok(Start), Start).Next;
        journal = journal.After(Ok(Start.AddSeconds(15)), Start.AddSeconds(15)).Next;

        Assert.Equal(
            "stopping: 2 snapshot(s) published since the last summary, the last at 08:00:15 UTC",
            Assert.Single(journal.Stopping()).Message);
    }
}
