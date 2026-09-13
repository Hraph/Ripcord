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

        List<string> lines =
        [
            $"A failover from {report.SourceHostName} to {report.TargetHostName} would not go "
            + "as planned.",
            "",
            $"raised at {Instant(raisedAt)}",
            $"checked at {Instant(report.EvaluatedAt)}",
        ];

        foreach (Finding finding in criticals)
        {
            lines.Add("");
            lines.Add(finding.Subject is { } vm
                ? $"{vm}: {finding.Rule.Title}"
                : finding.Rule.Title);
            lines.Add($"  seen: {finding.Observed}");
            lines.Add($"  on the day: {finding.Implication}");

            if (finding.Remedy is { } remedy)
            {
                lines.Add($"  fix: {remedy}");
            }
        }

        lines.Add("");
        lines.Add($"Run 'ripcord check' on {report.TargetHostName} for the full report.");

        return new Notification(
            AlertKind.Raised,
            $"ripcord: {count} on {report.SourceHostName} -> {report.TargetHostName}",
            string.Join("\n", lines));
    }

    internal static Notification Recovered(CheckReport report) =>
        new(
            AlertKind.Recovered,
            $"ripcord: {report.TargetHostName} is clear again",
            $"no critical rule is violated on {report.TargetHostName}.\n"
            + $"\nchecked at {Instant(report.EvaluatedAt)}");

    /// Local time with its offset spelled out. The reader is on a phone in another time zone
    /// as often as not, and "02:14" on its own is a guess.
    private static string Instant(DateTimeOffset instant) =>
        instant.ToString("yyyy-MM-dd HH:mm zzz", null);
}
