using System.Text;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;

namespace Ripcord.Cli.Rendering;

/// What gets read on the day. Criticals first, then the rules that could *not* be checked —
/// above the warnings, because an unverified critical is nearly as serious as a violated one
/// and the exit code cannot say so (decision D7).
///
/// Every finding prints three lines: what was observed, what it means at the moment of the
/// failover, and the command that fixes it. The middle one is the reason this tool exists.
public static class CheckRenderer
{
    /// Stated here as well as on `StatusRenderer`: the width is part of the contract with the
    /// screen, not an implementation detail.
    public const int Width = Layout.Width;

    private const int LabelColumn = 12;

    public static string Render(CheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        StringBuilder output = new();

        AppendHeader(output, report);
        AppendNotes(output, report);

        AppendSection(output, "CRITICAL", report.Of(Severity.Critical));
        AppendSection(output, "NOT CHECKED", report.Unevaluated);
        AppendSection(output, "WARNING", report.Of(Severity.Warning));
        AppendSection(output, "INFORMATION", report.Of(Severity.Info));

        AppendFeasibility(output, report);

        return output.ToString();
    }

    private static void AppendHeader(StringBuilder output, CheckReport report)
    {
        output.AppendLine(Layout.Banner("RIPCORD CHECK", Now(report)));
        output.AppendLine($"ripcord {BuildInfo.VersionWithCommit}");
        output.AppendLine();

        // The mode is the first thing to read: every other line means something different
        // depending on whether the pair is already running on the recovery side.
        AppendWrapped(output, "Mode", report.Mode switch
        {
            OperatingMode.FailedOver => $"FAILED OVER - running on {report.TargetHostName}",
            _ => "NORMAL",
        }, Layout.Indent);

        AppendWrapped(
            output,
            "Source",
            $"{report.SourceHostName} (holds the primary copies)",
            Layout.Indent);

        AppendWrapped(
            output,
            "Target",
            $"{report.TargetHostName} (a failover would land here)",
            Layout.Indent);

        AppendWrapped(output, "Verdict", Verdict(report), Layout.Indent);
        output.AppendLine();
    }

    /// Counted rather than implied. "Not checked" has to appear in the headline, because the
    /// exit code cannot carry it and nothing else would make an operator scroll.
    private static string Verdict(CheckReport report)
    {
        int criticals = report.Of(Severity.Critical).Count();
        int acknowledged = report.Of(Severity.Critical)
            .Count(finding => finding.Suppression is { IsActive: true });

        string counted =
            $"{criticals} critical"
            + (acknowledged > 0 ? $" ({acknowledged} acknowledged)" : "")
            + $", {report.Of(Severity.Warning).Count()} warning"
            + $", {report.Of(Severity.Info).Count()} info"
            + $", {report.Unevaluated.Count()} not checked";

        return report.HasCriticalViolation
            ? $"NOT READY - {counted}"
            : $"{counted}";
    }

    private static void AppendNotes(StringBuilder output, CheckReport report)
    {
        if (report.Notes.Count == 0)
        {
            return;
        }

        output.AppendLine("READING");

        foreach (string note in report.Notes)
        {
            AppendWrapped(output, "", note, Layout.Indent);
        }

        output.AppendLine();
    }

    private static void AppendSection(
        StringBuilder output, string title, IEnumerable<Finding> findings)
    {
        List<Finding> listed = [.. findings];

        if (listed.Count == 0)
        {
            return;
        }

        output.AppendLine($"{title} ({listed.Count})");
        output.AppendLine(Layout.Line(Layout.Width));

        foreach (Finding finding in listed)
        {
            AppendFinding(output, finding);
        }

        output.AppendLine();
    }

    private static void AppendFinding(StringBuilder output, Finding finding)
    {
        output.AppendLine(
            Layout.Spaces(Layout.Indent)
            + $"[{finding.Rule.Id}]"
            + (finding.Subject is { } subject ? $" {subject}" : ""));

        AppendWrapped(output, "Observed", finding.Observed, Layout.Indent + 2);

        // Unevaluable findings all share one implication, and repeating it under each of a
        // dozen lines would bury the observations that differ.
        if (finding.Verdict == FindingVerdict.Violated)
        {
            AppendWrapped(output, "On the day", finding.Implication, Layout.Indent + 2);
        }

        if (finding.Remedy is { } remedy)
        {
            AppendWrapped(output, "Fix", remedy, Layout.Indent + 2);
        }

        if (finding.Suppression is { } suppression)
        {
            AppendWrapped(output, "Accepted", Accepted(suppression), Layout.Indent + 2);
        }
    }

    /// A lapsed acknowledgement says so rather than letting the finding reappear with no
    /// explanation: the operator accepted this once, and the reason has expired.
    private static string Accepted(Suppression suppression)
    {
        Acknowledgement acknowledgement = suppression.Acknowledgement;

        return suppression.IsActive
            ? $"until {Layout.Date(acknowledgement.Expires)} - {acknowledgement.Reason}"
            : $"ACKNOWLEDGEMENT LAPSED on {Layout.Date(acknowledgement.Expires)} - "
                + acknowledgement.Reason;
    }

    private static void AppendFeasibility(StringBuilder output, CheckReport report)
    {
        Feasibility feasibility = report.Feasibility;

        output.AppendLine($"FEASIBILITY - {report.TargetHostName}");
        output.AppendLine(Layout.Line(Layout.Width));

        output.AppendLine(Layout.Spaces(Layout.Indent) + (feasibility.UsableRamMb is { } usable
            ? $"Usable memory: {usable} MB"
            : "Usable memory: unknown, the target's memory could not be read"));

        output.AppendLine(Row("VM", "PRI", "STARTUP", "MAX", "MIN", "BOOTS"));
        output.AppendLine(Layout.Spaces(Layout.Indent) + Layout.Line(Layout.Width - Layout.Indent));

        foreach (VmFeasibility vm in feasibility.Vms)
        {
            output.AppendLine(Row(
                Layout.Truncate(vm.Name, 20),
                vm.Priority.ToString(),
                Megabytes(vm.StartupRamMb),
                Megabytes(vm.DynamicMaximumMb),
                Megabytes(vm.DynamicMinimumMb),
                vm.WouldBoot switch
                {
                    true => "yes",
                    false => "NO",
                    _ => "?",
                }));
        }

        output.AppendLine(Layout.Spaces(Layout.Indent) + (feasibility.HeadroomMb is { } headroom
            ? $"Headroom after the VMs that would boot: {headroom} MB"
            : "Headroom: unknown"));
    }

    private static string Row(
        string name, string priority, string startup, string maximum, string minimum, string boots) =>
        Layout.Spaces(Layout.Indent)
        + Layout.Pad(name, 20) + ' '
        + Layout.Pad(priority, 4) + ' '
        + Layout.PadLeft(startup, 9) + ' '
        + Layout.PadLeft(maximum, 9) + ' '
        + Layout.PadLeft(minimum, 9) + "  "
        + boots;

    /// A blank column would read as zero; "unknown" is the answer.
    private static string Megabytes(int? value) => value is { } megabytes ? $"{megabytes} MB" : "?";

    private static void AppendWrapped(
        StringBuilder output, string label, string text, int indent)
    {
        string prefix = Layout.Spaces(indent)
            + (label.Length > 0 ? Layout.Pad(label + ":", LabelColumn) : "");

        string continuation = Layout.Spaces(prefix.Length);
        bool first = true;

        foreach (string line in Layout.Wrap(text, Layout.Width - prefix.Length))
        {
            output.AppendLine((first ? prefix : continuation) + line);
            first = false;
        }
    }

    /// The report has no clock of its own; the banner's instant is the one the engine judged
    /// with, carried in the feasibility of the moment rather than read again here.
    private static DateTimeOffset Now(CheckReport report) => report.EvaluatedAt;
}
