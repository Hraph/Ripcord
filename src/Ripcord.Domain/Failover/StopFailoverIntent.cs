using Ripcord.Domain.Replication;

namespace Ripcord.Domain.Failover;

/// What invoking `Stop-VMFailover` would actually do. One of these turns production off.
public enum StopFailoverEffect
{
    /// Replication is running normally on both sides. There is no failover to stop, and that
    /// is a reading in its own right — not the same as being unable to tell.
    NothingToStop,

    /// A test copy is destroyed. Milestone 3's cleanup, and the only benign one this type can
    /// currently name.
    DeletesTestCopy,

    /// The replica is serving production. Stopping turns it off and discards the failover.
    CancelsRealFailover,
}

/// `Stop-VMFailover` has three documented behaviours and no parameter that selects between
/// them: the one it performs depends on the state of the VM it is aimed at. One of the three
/// is destructive. So the effect is resolved from what both hosts report, before anything is
/// invoked, and an effect that cannot be resolved is refused rather than guessed.
///
/// Refusing is safe here because the operator keeps the manual command. Guessing is not: the
/// difference between the benign readings and the destructive one is production staying up.
///
/// **Not yet resolvable**: the third documented behaviour — cancelling a planned failover that
/// was started with `-Prepare`, which is the rollback path when a later step fails. The
/// `ReplicationState` a primary reports after `-Prepare` is not established (V35), and naming
/// it by guesswork is the one thing this type exists to avoid. Until it is verified, that
/// situation reaches the refusal branch and reports what it saw, which both keeps the operator
/// unblocked and records the state value that is missing.
public sealed record StopFailoverIntent(StopFailoverEffect? Effect, string Observed)
{
    /// The effect could not be established. Never an invocation.
    public bool IsRefused => this.Effect is null;

    public bool IsDestructive => this.Effect == StopFailoverEffect.CancelsRealFailover;

    public static StopFailoverIntent Resolve(
        VmReplicationState? onTarget, VmReplicationState? onSource)
    {
        if (onTarget is null)
        {
            return Refuse("the target host did not report this VM at all");
        }

        if (onTarget.Role == ReplicationRole.Unknown || onTarget.State == ReplicationState.Unknown)
        {
            return Refuse(
                $"the target reports role {onTarget.Role} and state {onTarget.State}, one of "
                + "which this binary could not map");
        }

        // Either field alone establishes a test copy: requiring both would refuse a real test
        // failover whenever one of the two readings was the less specific one.
        bool testCopy = onTarget.Role == ReplicationRole.TestReplica
            || onTarget.State == ReplicationState.FiredrillInProgress;

        bool realFailover =
            onTarget.State is ReplicationState.Recovered or ReplicationState.Committed;

        // A VM cannot be a copy made for a drill and the live production workload at once.
        // Contradictory readings are precisely when guessing costs the most.
        if (testCopy && realFailover)
        {
            return Refuse(
                $"the target reports role {onTarget.Role} and state {onTarget.State} together, "
                + "which describes a test copy and a real failover at the same time");
        }

        if (testCopy)
        {
            return new StopFailoverIntent(StopFailoverEffect.DeletesTestCopy,
                $"a test copy is running on the target, reported as {onTarget.State}");
        }

        if (realFailover)
        {
            return new StopFailoverIntent(StopFailoverEffect.CancelsRealFailover,
                $"the target is serving this VM after a failover, reported as {onTarget.State}");
        }

        // "Nothing to stop" needs positive evidence from both sides, not the absence of
        // evidence for the other two. A primary part-way through a prepared failover reports
        // neither of the states above, and letting it fall through to here would answer
        // "nothing to stop" about the one situation with something very much to stop.
        if (onTarget.State == ReplicationState.Replicating
            && onSource is { Role: ReplicationRole.Primary, State: ReplicationState.Replicating })
        {
            return new StopFailoverIntent(StopFailoverEffect.NothingToStop,
                "both sides are replicating normally");
        }

        return Refuse(
            $"the target reports {onTarget.State} and the source "
            + (onSource is null
                ? "was not read"
                : $"reports role {onSource.Role} and state {onSource.State}")
            + ", a combination this binary cannot resolve to one behaviour");
    }

    private static StopFailoverIntent Refuse(string observed) => new(null, observed);
}
