using System.Globalization;
using System.Text;
using Ripcord.Application.TestFailover;
using Ripcord.Domain.Checks;
using Ripcord.Domain.TestFailover;

namespace Ripcord.Cli.Rendering;

/// The monthly operations note, before anyone types it up. It has to be readable at a glance
/// and it has to be honest at a glance: "booted" and "booted, unconfirmed" are different
/// claims, and the second must never be skimmed as the first.
///
/// Same constraints as the other two renderers — fixed columns, 1024×768, no colour — because
/// this one is read in the same session as `check`, one after the other.
public static class TestFailoverRenderer
{
    private const int NameColumn = 20;
    private const int OutcomeColumn = 24;
    private const int BootColumn = 8;

    public static string Render(TestFailoverReport report, string? testSwitch)
    {
        ArgumentNullException.ThrowIfNull(report);

        StringBuilder output = new();

        output.AppendLine(Layout.Banner(
            report.DryRun ? "RIPCORD TEST FAILOVER (DRY RUN)" : "RIPCORD TEST FAILOVER",
            report.StartedAt));
        output.AppendLine($"ripcord {BuildInfo.VersionWithCommit}");
        output.AppendLine();

        if (report.Unattended)
        {
            AppendField(
                output,
                "Mode",
                "unattended - no operator confirmed this run, so every unchecked rule "
                    + "blocked it");
        }

        AppendField(output, "Isolation", testSwitch is null
            ? "every adapter disconnected (no test_failover_switch configured)"
            : $"'{testSwitch}', which must not be an external switch");

        output.AppendLine();

        AppendRefusal(output, report.Refusal);
        AppendResults(output, report);
        AppendUnconfirmed(output, report.UnconfirmedDiskSets);
        AppendOrphans(output, report.Orphans);
        AppendVerdict(output, report);

        return Layout.Rendered(output);
    }

    private static void AppendRefusal(StringBuilder output, PreconditionRefusal? refusal)
    {
        if (refusal is not { Refuses: true })
        {
            return;
        }

        AppendHeading(output, "REFUSED - NOTHING WAS CHANGED");

        foreach (Finding finding in refusal.Criticals)
        {
            AppendWrapped(output, $"critical: {finding.Rule.Title}");
            AppendWrapped(output, finding.Observed, Layout.Indent * 2);
        }

        // Stated separately and in these words: a rule nobody could check is not a rule that
        // passed, and the exit code of `check` cannot make that distinction (decision D7).
        foreach (Finding finding in refusal.Unevaluated)
        {
            AppendWrapped(output, $"not checked: {finding.Rule.Title}");
            AppendWrapped(output, finding.Observed, Layout.Indent * 2);
        }

        output.AppendLine();
    }

    private static void AppendResults(StringBuilder output, TestFailoverReport report)
    {
        if (report.Results.Count == 0)
        {
            return;
        }

        AppendHeading(output, report.DryRun ? "WOULD BE TESTED" : "RESULT");

        output.AppendLine(
            Layout.Spaces(Layout.Indent)
            + Layout.Pad("VM", NameColumn) + ' '
            + Layout.Pad("OUTCOME", OutcomeColumn) + ' '
            + Layout.Pad("BOOT", BootColumn) + ' '
            + "CLEANUP");

        foreach (VmTestFailoverResult result in report.Results)
        {
            output.AppendLine(
                Layout.Spaces(Layout.Indent)
                + Layout.Pad(Layout.Truncate(result.Name, NameColumn), NameColumn) + ' '
                + Layout.Pad(Outcome(result.Status), OutcomeColumn) + ' '
                + Layout.Pad(Duration(result.TimeToHeartbeat), BootColumn) + ' '
                + Cleanup(result.Cleanup));

            AppendDetail(output, result);
        }

        output.AppendLine();
    }

    private static void AppendDetail(StringBuilder output, VmTestFailoverResult result)
    {
        foreach (IsolationBreach breach in result.Breaches)
        {
            AppendWrapped(output, $"{breach.Adapter}: {breach.Observed}", Layout.Indent * 2);
        }

        if (result.FailureMessage is { } message && result.Breaches.Count == 0)
        {
            AppendWrapped(output, message, Layout.Indent * 2);
        }

        if (result.Cleanup is { Succeeded: false, FailureMessage: { } failure })
        {
            AppendWrapped(
                output,
                $"the test VM was NOT destroyed: {failure}. It holds disk on this host and "
                    + "will break the next test until it is removed.",
                Layout.Indent * 2);
        }
    }

    /// Deliberately not a refusal. Neither rule affects whether an isolated copy boots, so
    /// blocking on it would refuse a test for a fact that does not change the test's
    /// validity — but a green boot must not be read as a complete guest.
    private static void AppendUnconfirmed(StringBuilder output, IReadOnlyList<string> vmNames)
    {
        if (vmNames.Count == 0)
        {
            return;
        }

        AppendHeading(output, "DISK SET NOT CONFIRMED");

        AppendWrapped(
            output,
            string.Join(", ", vmNames)
                + " - the disk set of these VMs is incomplete or could not be read. They boot "
                + "their operating system either way, so a green boot above does not mean a "
                + "complete guest.");

        output.AppendLine();
    }

    private static void AppendOrphans(StringBuilder output, IReadOnlyList<Orphan> orphans)
    {
        if (orphans.Count == 0)
        {
            return;
        }

        AppendHeading(output, "TEST VMs LEFT BEHIND BY AN EARLIER RUN");

        foreach (Orphan orphan in orphans)
        {
            output.AppendLine(
                Layout.Spaces(Layout.Indent)
                + Layout.Pad(Layout.Truncate(orphan.Name, NameColumn), NameColumn) + ' '
                + (orphan.Verdict == OrphanVerdict.AgeUnknown
                    ? "age unknown"
                    : $"{Duration(orphan.Age)} old"));
        }

        AppendWrapped(
            output,
            "Hyper-V lists these and never mentions them. Each one holds disk on this host.",
            Layout.Indent);

        output.AppendLine();
    }

    private static void AppendVerdict(StringBuilder output, TestFailoverReport report)
    {
        output.AppendLine(Layout.Line(Layout.Width));

        if (report.DryRun)
        {
            AppendWrapped(
                output, "Nothing was changed. Re-run without --dry-run to test for real.");
            return;
        }

        if (report.Refusal is { Refuses: true })
        {
            AppendWrapped(output, "Nothing was changed.");
            return;
        }

        if (report.Interrupted)
        {
            AppendWrapped(
                output,
                "Interrupted. Every test VM created by this run was destroyed; check the "
                    + "cleanup column above.");
            return;
        }

        int booted = report.Results.Count(
            result => result.Status == TestFailoverStatus.Booted);

        AppendWrapped(
            output,
            $"{booted} of {report.Results.Count} booted and were confirmed by heartbeat.");
    }

    private static string Outcome(TestFailoverStatus status) => status switch
    {
        TestFailoverStatus.Booted => "booted",

        // Deliberately not the word "booted" on its own: the guest could not be asked, so
        // the host observed a power-on and nothing more.
        TestFailoverStatus.BootedWithoutHeartbeat => "started, UNCONFIRMED",

        TestFailoverStatus.NoHeartbeat => "NO HEARTBEAT",
        TestFailoverStatus.RefusedNotIsolated => "REFUSED - not isolated",
        TestFailoverStatus.Failed => "FAILED",
        TestFailoverStatus.Skipped => "not attempted",
        _ => "would be tested",
    };

    private static string Cleanup(Compensation? cleanup) => cleanup switch
    {
        null => "-",
        { Succeeded: true } => "done",
        _ => "FAILED",
    };

    private static string Duration(TimeSpan? duration) =>
        duration is not { } value
            ? "-"
            : value.TotalHours >= 1
                ? ((int)value.TotalHours).ToString(CultureInfo.InvariantCulture) + "h"
                : ((int)value.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";

    private static void AppendHeading(StringBuilder output, string heading)
    {
        output.AppendLine(heading);
        output.AppendLine(Layout.Line(heading.Length));
    }

    private static void AppendField(StringBuilder output, string label, string value)
    {
        output.AppendLine(
            Layout.Spaces(Layout.Indent) + Layout.Pad(label, 12) + ' '
            + Layout.Truncate(value, Layout.Width - Layout.Indent - 13));
    }

    private static void AppendWrapped(StringBuilder output, string text, int indent = 2)
    {
        foreach (string line in Layout.Wrap(text, Layout.Width - indent))
        {
            output.AppendLine(Layout.Spaces(indent) + line);
        }
    }
}
