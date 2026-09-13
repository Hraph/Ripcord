using Ripcord.Domain.Checks;

namespace Ripcord.Domain.Alerting;

public enum AlertKind
{
    Raised,
    Recovered,
}

/// One message, whatever carries it. Written for a phone at 3 a.m.: the finding and what it
/// means on the day of the failover, never a dump of the check output — the full report is a
/// command away, and a notification nobody finishes reading is a notification nobody reads.
public sealed record Notification(AlertKind Kind, string Subject, string Body)
{
    internal static Notification Raised(
        CheckReport report, IReadOnlyList<Finding> criticals, DateTimeOffset raisedAt)
    {
        string count = criticals.Count == 1
            ? "1 critical finding"
            : $"{criticals.Count} critical findings";

        // Every name in here was read off a host. A newline in one would forge a line of its
        // own in a message whose whole point is that each line is a finding.
        List<string> lines =
        [
            $"A failover from {Printable.Of(report.SourceHostName)} to "
            + $"{Printable.Of(report.TargetHostName)} would not go as planned.",
            "",
            $"raised at {Instant(raisedAt)}",
            $"checked at {Instant(report.EvaluatedAt)}",
        ];

        foreach (Finding finding in criticals)
        {
            lines.Add("");
            lines.Add(finding.Subject is { } vm
                ? $"{Printable.Of(vm)}: {finding.Rule.Title}"
                : finding.Rule.Title);
            lines.Add($"  seen: {Printable.Of(finding.Observed)}");
            lines.Add($"  on the day: {Printable.Of(finding.Implication)}");

            if (finding.Remedy is { } remedy)
            {
                lines.Add($"  fix: {Printable.Of(remedy)}");
            }
        }

        lines.Add("");
        lines.Add(
            $"Run 'ripcord check' on {Printable.Of(report.TargetHostName)} for the full report.");

        // The subject becomes a mail header, and the host names in it were read off the pair.
        // A carriage return in one of them would be a header of somebody else's choosing.
        return new Notification(
            AlertKind.Raised,
            Printable.Of(
                $"ripcord: {count} on {report.SourceHostName} -> {report.TargetHostName}"),
            string.Join("\n", lines));
    }

    internal static Notification Recovered(CheckReport report) =>
        new(
            AlertKind.Recovered,
            Printable.Of($"ripcord: {report.TargetHostName} is clear again"),
            $"no critical rule is violated on {Printable.Of(report.TargetHostName)}.\n"
            + $"\nchecked at {Instant(report.EvaluatedAt)}");

    /// Local time with its offset spelled out. The reader is on a phone in another time zone
    /// as often as not, and "02:14" on its own is a guess.
    private static string Instant(DateTimeOffset instant) =>
        instant.ToString("yyyy-MM-dd HH:mm zzz", null);
}
