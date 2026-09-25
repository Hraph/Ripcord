using Ripcord.Domain;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Deployment;

/// Reading the listener's log back: which files, and what the last run said about its end.
public class ListenerLogTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 25, 0, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Candidates_are_today_then_yesterday_in_utc()
    {
        IReadOnlyList<string> candidates = ListenerLog.Candidates(
            @"C:\Program Files\Ripcord\logs", At.ToOffset(TimeSpan.FromHours(-5)));

        Assert.Equal(
            [
                @"C:\Program Files\Ripcord\logs\listener-2026-09-25.log",
                @"C:\Program Files\Ripcord\logs\listener-2026-09-24.log",
            ],
            candidates);
    }

    [Fact]
    public void The_exit_is_read_as_the_command_wrote_it()
    {
        ListenerLogSummary summary = ListenerLog.Summarise(
        [
            .. ListenerStartup.Banner("0.4.1", "ripcord.yaml").Render(At),
            .. CommandEntries.Exited("serve", ExitCode.InvalidConfiguration).Render(At),
        ]);

        Assert.Equal(new ListenerLogSummary(true, ExitCode.InvalidConfiguration, false, false), summary);
    }

    [Fact]
    public void A_console_line_that_mentions_an_exit_is_not_one()
    {
        ListenerLogSummary summary = ListenerLog.Summarise(
            DiagnosticEntry.Of("serve", "exit 2: of the building").Render(At));

        Assert.Null(summary.Exit);
    }

    [Fact]
    public void A_banner_with_nothing_after_it_ended_silently()
    {
        ListenerLogSummary summary = ListenerLog.Summarise(
            ListenerStartup.Banner("0.4.1", "ripcord.yaml").Render(At));

        Assert.True(summary.EndedSilently);
    }

    [Fact]
    public void Without_a_banner_the_whole_tail_is_the_last_run()
    {
        ListenerLogSummary summary = ListenerLog.Summarise(
            CommandEntries.Crashed("serve", "System.IO.IOException: gone").Render(At));

        Assert.False(summary.SawStart);
        Assert.True(summary.Crashed);
    }

    [Fact]
    public void Detail_lines_are_never_read_as_entries()
    {
        ListenerLogSummary summary = ListenerLog.Summarise(["    x: exit 2: InvalidConfiguration"]);

        Assert.Equal(ListenerLogSummary.Empty, summary);
    }
}
