using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;

namespace Ripcord.Domain.Checks;

/// Everything `ripcord check` was asked to judge. No IO, no WMI, no implicit clock: the
/// instant is passed in, so the whole engine is deterministic and every rule is reachable
/// from a test.
public sealed record CheckRequest(
    PairView View,
    RipcordConfiguration Configuration,
    DateTimeOffset Now,
    IReadOnlyList<string> Notes);

/// Everything it decided. The exit code is here rather than in the CLI, because "is this
/// infrastructure ready" is a decision and decisions live in the Domain.
public sealed record CheckReport(
    OperatingMode Mode,
    string SourceHostName,
    string TargetHostName,
    bool TargetIsLocal,
    IReadOnlyList<Finding> Findings,
    Feasibility Feasibility,
    IReadOnlyList<string> Notes)
{
    public IEnumerable<Finding> Violations =>
        this.Findings.Where(finding => finding.Verdict == FindingVerdict.Violated);

    public IEnumerable<Finding> Unevaluated =>
        this.Findings.Where(finding => finding.Verdict == FindingVerdict.Unevaluable);

    public IEnumerable<Finding> Of(Severity severity) =>
        this.Violations.Where(finding => finding.Rule.Severity == severity);

    public bool HasCriticalViolation => this.Findings.Any(finding => finding.CountsAsCritical);

    /// Warnings never change the exit code: a scheduled task that failed on a warning would
    /// be switched off within a week, and then the criticals would go unread too.
    public ExitCode Code =>
        this.HasCriticalViolation ? ExitCode.CriticalFinding : ExitCode.Success;
}

/// The rule engine. A `PairView` in, a list of findings out — pure, so the sequences it
/// decides are exercisable without Windows and without a pair.
public static class CheckEngine
{
    public static CheckReport Evaluate(CheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        CheckSubject subject =
            CheckSubject.From(request.View, request.Configuration, request.Now);

        Feasibility feasibility = Feasibility.Calculate(
            request.Configuration.Vms,
            subject.Target,
            request.Configuration.Node.HostMemoryReserveGb);

        List<Finding> findings =
        [
            .. NetworkRules.Evaluate(subject),
            .. CapacityRules.Evaluate(subject, feasibility),
            .. StorageRules.Evaluate(subject),
            .. ReplicationRules.Evaluate(subject),
            .. StandingRules.Evaluate(subject),
        ];

        return new CheckReport(
            subject.Mode,
            subject.Source.HostName,
            subject.Target.HostName,
            subject.TargetIsLocal,
            Ordered(Acknowledged(findings, request.Configuration, request.Now)),
            feasibility,
            request.Notes);
    }

    /// Acknowledgements apply to violations only: accepting a rule that could not be
    /// evaluated would suppress the admission that it was not checked.
    ///
    /// A lapsed acknowledgement is attached all the same, so the report can say the reason
    /// expired rather than let the finding reappear with no explanation.
    private static List<Finding> Acknowledged(
        List<Finding> findings, RipcordConfiguration configuration, DateTimeOffset now)
    {
        List<Finding> acknowledged = [];

        foreach (Finding finding in findings)
        {
            Acknowledgement? match = finding.Verdict == FindingVerdict.Violated
                ? configuration.Acknowledgements.FirstOrDefault(
                    entry => entry.Covers(finding.Rule.Id, finding.Subject))
                : null;

            acknowledged.Add(match is null
                ? finding
                : finding with { Suppression = new Suppression(match, match.IsActiveAt(now)) });
        }

        return acknowledged;
    }

    /// Catalogue order, then subject. The report is read top to bottom under pressure, so the
    /// order has to be the same on both hosts and from one run to the next.
    private static List<Finding> Ordered(List<Finding> findings) =>
        [
            .. findings
                .OrderBy(finding => finding.Rule.Severity)
                .ThenBy(finding => IndexOf(finding.Rule))
                .ThenBy(finding => finding.Subject ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(finding => finding.Observed, StringComparer.Ordinal),
        ];

    private static int IndexOf(CheckRule rule)
    {
        for (int index = 0; index < CheckRules.All.Count; index++)
        {
            if (CheckRules.All[index].Id == rule.Id)
            {
                return index;
            }
        }

        return CheckRules.All.Count;
    }
}
