using Ripcord.Domain.Checks;

namespace Ripcord.Domain.Failover;

/// Why an operation will not be attempted, and what it is proceeding past when it is. All
/// three lists are carried rather than reduced to a flag: "refused" without "because" is a
/// refusal nobody can act on, and an operation whose gate is deliberately empty still owes the
/// operator everything it decided not to stop for.
///
/// Deliberately separate from milestone 3's `PreconditionRefusal`, which carries no
/// `Proceeded` because a test failover has no operation that proceeds past a critical.
public sealed record FailoverRefusal(
    IReadOnlyList<Finding> Criticals,
    IReadOnlyList<Finding> Unevaluated,
    IReadOnlyList<Finding> Proceeded)
{
    public bool Refuses => this.Criticals.Count > 0 || this.Unevaluated.Count > 0;

    public bool Equals(FailoverRefusal? other) =>
        other is not null
        && Structural.Same(this.Criticals, other.Criticals)
        && Structural.Same(this.Unevaluated, other.Unevaluated)
        && Structural.Same(this.Proceeded, other.Proceeded);

    public override int GetHashCode()
    {
        HashCode hash = new();
        Structural.Add(ref hash, this.Criticals);
        Structural.Add(ref hash, this.Unevaluated);
        Structural.Add(ref hash, this.Proceeded);
        return hash.ToHashCode();
    }
}

public enum FailoverOperation
{
    PlannedFailover,
    UnplannedFailover,
    Failback,
    Reprotect,
}

/// The gate in front of each mutating operation, expressed as one written-out list per
/// operation rather than one shared predicate.
///
/// The lists look repetitive and that repetition is deliberate. Whether an unknown is
/// peripheral is a property of the **pair** of fact and operation, never of the fact alone:
/// free space is peripheral to whether an unplanned failover should proceed, and not remotely
/// peripheral to whether the VMs stay running once it has. Factoring the four into one set
/// would force some rule into a class it does not belong to, with no honest place to put it.
///
/// A list rather than a predicate for the same reason milestone 3 used one: a list can be
/// disagreed with by somebody who was not in the room, and a predicate can only be
/// reverse-engineered.
///
/// **Decided by a session, unarbitrated.** The criterion applied throughout is "would
/// proceeding on this unknown make the outcome worse, or less recoverable, than not acting at
/// all" — not "is this rule important". Two consequences a reader may want to argue with are
/// marked below.
public static class FailoverPrecondition
{
    /// Elective, and postponing costs nothing. So anything that decides whether the VM comes
    /// up correctly on the other side stops it.
    ///
    /// The disk rules are here for the recoverability half of the criterion: a disk that was
    /// not confirmed on the way over is a disk that is not there on the way back, and failback
    /// is where this milestone is actually tested.
    public static readonly IReadOnlyList<string> BlockingForPlannedFailover =
    [
        CheckRules.ReplicaSwitchMismatch,
        CheckRules.ReplicaAdapterDisconnected,
        CheckRules.ReplicaMacDrift,
        CheckRules.VlanMismatch,
        CheckRules.StartupRamExceedsTarget,
        CheckRules.P1StartupRamSumExceedsTarget,
        CheckRules.DynamicMaximumExceedsTarget,
        CheckRules.FreeSpaceBelowThreshold,
        CheckRules.VhdxOutsideRelationship,
        CheckRules.PassthroughDiskOnReplicatedVm,
    ];

    /// **Empty, and that is the decision rather than an omission.** An unplanned failover
    /// happens because production is already down; the primary being unreachable is the
    /// definition of the scenario, not a surprise. Every unknown here is therefore an unknown
    /// about a machine nobody can consult, and refusing on any of them means leaving
    /// production down to protect it from booting imperfectly.
    ///
    /// Nothing in the report can make the outcome worse than not failing over at all. What
    /// does stop this operation lives elsewhere and is not about unreadable facts:
    /// `SplitBrain` halts it, because two live claimants is the one state no sequence repairs,
    /// and the operator still types the confirmation after reading the impact summary.
    ///
    /// Findings are reported rather than swallowed — see `FailoverRefusal.Proceeded`.
    public static readonly IReadOnlyList<string> BlockingForUnplannedFailover = [];

    /// Failback is the planned sequence pointed homewards, so it inherits the planned list.
    public static readonly IReadOnlyList<string> BlockingForFailback = BlockingForPlannedFailover;

    /// Reprotect re-establishes replication, which drives a seeded initial replication onto
    /// the host that is now the replica. Not reprotecting leaves the pair unprotected, which
    /// is bad and stable. Reprotecting into a volume that turns out to be full pauses the VMs
    /// currently serving production, which is worse — so the storage unknowns stop it and the
    /// rest do not.
    public static readonly IReadOnlyList<string> BlockingForReprotect =
    [
        CheckRules.FreeSpaceBelowThreshold,
        CheckRules.TargetBitlockerWithoutAutounlock,
    ];

    /// Rules a given operation must not gate on, because they describe the very state it
    /// exists to leave. Decision D20 in a mutating command: "replication direction inverted"
    /// is true by definition while running on DR, so a failback gated on it is blocked by the
    /// state it was invoked to repair.
    ///
    /// Scoped to the rule, never to the severity — a failback still stops for a critical it
    /// has nothing to do with.
    private static readonly IReadOnlyList<string> InvertedDirectionExemption =
    [
        CheckRules.ReplicationDirectionInverted,
    ];

    /// `subject` is the VM being acted on. Findings about a *different* VM do not gate it.
    ///
    /// Without that scope the gate is unusable on this infrastructure rather than merely
    /// strict: `VM-BACKUP-01` carries a pass-through disk Hyper-V Replica cannot replicate, so
    /// some of its facts are permanently unreadable — and an unscoped gate would let that block
    /// every planned failover of the domain controller, for ever, for a reason that has nothing
    /// to do with the domain controller.
    ///
    /// Host-level findings carry no subject and always apply: free space on the target is not
    /// about one VM, and the VM being moved lands on the same volume as the rest.
    ///
    /// A null subject means "judge the whole pair", which is what a sweep needs.
    public static FailoverRefusal Evaluate(
        CheckReport report, FailoverOperation operation, string? subject = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        IReadOnlyList<string> blocking = BlockingFor(operation);
        IReadOnlyList<string> exempt = ExemptFor(operation);

        bool About(Finding finding) =>
            subject is null
            || finding.Subject is null
            || string.Equals(finding.Subject, subject, StringComparison.OrdinalIgnoreCase);

        List<Finding> criticals =
        [
            .. report.Findings.Where(finding =>
                finding.CountsAsCritical && About(finding) && !exempt.Contains(finding.Rule.Id)),
        ];

        List<Finding> unevaluated =
        [
            .. report.Unevaluated.Where(finding =>
                About(finding) && blocking.Contains(finding.Rule.Id)),
        ];

        // Everything the operation is about to proceed past. Swallowing it would be the tool
        // deciding on the operator's behalf: the impact summary and the typed confirmation are
        // what stand in for a gate when the gate is deliberately empty.
        List<Finding> proceeded =
        [
            .. report.Findings.Where(finding =>
                !criticals.Contains(finding) && !unevaluated.Contains(finding)),
        ];

        return new FailoverRefusal(criticals, unevaluated, proceeded);
    }

    private static IReadOnlyList<string> BlockingFor(FailoverOperation operation) =>
        operation switch
        {
            FailoverOperation.PlannedFailover => BlockingForPlannedFailover,
            FailoverOperation.UnplannedFailover => BlockingForUnplannedFailover,
            FailoverOperation.Failback => BlockingForFailback,
            FailoverOperation.Reprotect => BlockingForReprotect,
            _ => BlockingForPlannedFailover,
        };

    /// An unplanned failover gates on no critical either, for the same reason its blocking
    /// list is empty: production is already down, and a critical describes an imperfect boot
    /// rather than a reason to stay down.
    private static IReadOnlyList<string> ExemptFor(FailoverOperation operation) =>
        operation switch
        {
            FailoverOperation.UnplannedFailover => [.. CheckRules.All.Select(rule => rule.Id)],
            FailoverOperation.Failback or FailoverOperation.Reprotect
                => InvertedDirectionExemption,
            _ => [],
        };
}
