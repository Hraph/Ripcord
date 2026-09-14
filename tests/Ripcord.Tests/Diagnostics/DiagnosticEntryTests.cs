using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Diagnostics;

/// What a line of the log looks like. Read with `type` on the same console every other rule
/// protects, and pasted into a ticket afterwards, so it is plain text and one line per line.
public class DiagnosticEntryTests
{
    private static readonly DateTimeOffset At =
        new(2026, 9, 14, 8, 30, 12, 345, TimeSpan.FromHours(2));

    /// UTC, whatever the host's locale: the two sides of a pair are compared line by line the
    /// morning after, and a local offset each makes that arithmetic.
    [Fact]
    public void The_stamp_is_utc_and_sortable()
    {
        Assert.Equal(
            "2026-09-14 06:30:12.345Z status: cannot read the local Hyper-V state",
            DiagnosticEntry.Of("status", "cannot read the local Hyper-V state").Render(At)[0]);
    }

    /// A stack trace is one line per frame, indented under the sentence it explains.
    [Fact]
    public void A_detail_is_split_into_lines_and_indented()
    {
        IReadOnlyList<string> rendered = DiagnosticEntry
            .Of("status", "failed", "CimException: type mismatch\r\n   at Ripcord.Wmi.Read()")
            .Render(At);

        Assert.Equal(
            [
                "2026-09-14 06:30:12.345Z status: failed",
                "    CimException: type mismatch",
                "       at Ripcord.Wmi.Read()",
            ],
            rendered);
    }

    /// Same reasoning as the console: this file is read in a terminal, and an escape sequence
    /// in a VM name that crossed the pair channel can paint over the line above it.
    [Fact]
    public void Text_that_could_move_a_cursor_is_neutralised()
    {
        Assert.Equal(
            "2026-09-14 06:30:12.345Z status: VM?[2K gone",
            DiagnosticEntry.Of("status", "VM\u001b[2K gone").Render(At)[0]);
    }

    /// The redaction is not something a caller has to remember: it is applied on the way out,
    /// so a message assembled anywhere in the tool cannot carry a token into the file.
    [Fact]
    public void Secrets_are_removed_by_the_rendering_rather_than_by_the_caller()
    {
        Assert.Equal(
            "2026-09-14 06:30:12.345Z check: posting to https://hooks.slack.com/[removed] failed",
            DiagnosticEntry
                .Of("check", "posting to https://hooks.slack.com/services/T0/B0/zz failed")
                .Render(At)[0]);
    }
}
