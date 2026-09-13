using Ripcord.Domain.Checks;

namespace Ripcord.Domain.TestFailover;

/// Why a test failover will not be attempted. Both lists are carried rather than reduced to a
/// flag: the operator has to be told what to fix, and "refused" without "because" is a
/// refusal nobody can act on.
public sealed record PreconditionRefusal(
    IReadOnlyList<Finding> Criticals, IReadOnlyList<Finding> Unevaluated)
{
    public bool Refuses => this.Criticals.Count > 0 || this.Unevaluated.Count > 0;
}

/// Step 1 of the sequence, as a gate.
///
/// `CheckReport.HasCriticalViolation` is not sufficient on its own, and deliberately so: it
/// counts only *violated* criticals, because code 1 means "a critical rule is violated" and
/// overloading it with "could not be checked" would make `check` unusable as the gate the
/// later milestones depend on. Gating on it alone would happily start a test failover on a
/// pair Ripcord could not actually inspect.
///
/// So the gate adds a second list — and it is a list, written out below, not a predicate. A
/// list can be disagreed with by someone who was not in the room; a predicate can only be
/// reverse-engineered. Refusing on *every* unevaluable finding would be the other failure:
/// an unreadable certificate expiry has no bearing on whether an isolated copy boots.
public static class TestFailoverPrecondition
{
    /// Rules whose *unevaluability* stops a test failover, each because a test VM is a real
    /// VM consuming real resources on the target — not because the rule itself is important.
    public static readonly IReadOnlyList<string> BlockingWhenUnevaluated =
    [
        // Memory that could not be read is not memory that fits. The target has about
        // twelve gigabytes usable and a test VM takes its startup RAM out of that.
        CheckRules.StartupRamExceedsTarget,
        CheckRules.P1StartupRamSumExceedsTarget,

        // The ceiling the test VM may grow to once it is running.
        CheckRules.DynamicMaximumExceedsTarget,

        // Both are reads of the same adapter data the isolation check needs. Unreadable
        // here means unreadable there, and refusing before anything is created is cheaper
        // than creating a test failover only to destroy it again.
        CheckRules.ReplicaSwitchMismatch,
        CheckRules.ReplicaAdapterDisconnected,

        // A running test VM writes its differencing data to the target's volume.
        CheckRules.FreeSpaceBelowThreshold,
    ];

    /// Rules that say whether a VM would boot with all of its disks. They deliberately do
    /// **not** block: neither consumes memory or storage on the target, and neither affects
    /// whether an isolated copy comes up — refusing on them would refuse a test for a fact
    /// that does not change the test's validity.
    ///
    /// They are surfaced instead, because a guest missing a data disk boots its operating
    /// system and answers the heartbeat perfectly well. Without this line the report would
    /// say "booted" about a VM that is silently incomplete, and milestone 3 is meant to be
    /// the one end-to-end test that does not flatter the infrastructure.
    private static readonly IReadOnlyList<string> DiskCompletenessRules =
    [
        CheckRules.PassthroughDiskOnReplicatedVm,
        CheckRules.VhdxOutsideRelationship,
    ];

    /// VMs whose disk set is either known to be incomplete or could not be confirmed.
    public static IReadOnlyList<string> UnconfirmedDiskSets(CheckReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return
        [
            .. report.Findings
                .Where(finding => DiskCompletenessRules.Contains(finding.Rule.Id))
                .Select(finding => finding.Subject)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// Unattended, the gate tightens rather than loosens. The list above exists because a
    /// human reading the report can weigh an unevaluable certificate expiry and proceed;
    /// nobody is reading it at 2am, so every unevaluable finding stops the run. That is the
    /// counterweight to skipping the typed confirmation, not a separate policy.
    public static PreconditionRefusal Evaluate(CheckReport report, bool unattended = false)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new PreconditionRefusal(
            [.. report.Findings.Where(finding => finding.CountsAsCritical)],

            // Acknowledgements are attached to violations only, by design, so nothing here
            // can be waved through: an operator cannot silence an admission of ignorance.
            [.. report.Unevaluated.Where(finding =>
                unattended || BlockingWhenUnevaluated.Contains(finding.Rule.Id))]);
    }
}
