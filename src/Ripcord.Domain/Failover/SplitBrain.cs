using Ripcord.Domain.Replication;

namespace Ripcord.Domain.Failover;

public enum SplitBrainVerdict
{
    NotSuspected,

    /// Two hosts both claim the same VM. Every mutating command stops here.
    Suspected,

    /// One of the two readings could not be mapped, so neither claim can be judged. Its own
    /// answer rather than a quiet "not suspected": a role this binary does not recognise is
    /// not evidence that nothing is wrong.
    Indeterminate,
}

/// Whether both hosts believe they own the same VM. There is no sequence that recovers from
/// this — both sides are writing, and whichever copy is discarded takes real work with it — so
/// it gates every mutating operation rather than appearing as a finding to read later.
///
/// Judged from replication role, replication state and power state, on both hosts. It covers
/// the three documented routes: two copies powered on at once, both sides claiming primary,
/// and a live original primary beside a target that has failed over.
///
/// It does not claim to detect every split brain there could be — a divergence that shows in
/// none of those three readings is invisible to it — and saying so is better than letting the
/// type's name imply more.
///
/// The asymmetry that matters: a missed split brain destroys data, and a phantom one blocks
/// the failover during an incident. So a claim needs a positive reading on **both** sides.
/// Silence is not a claim — during an unplanned failover the primary is gone by definition,
/// and treating its absence as a claim would refuse the failover in the one case the tool
/// exists for.
public sealed record SplitBrain(SplitBrainVerdict Verdict, string? Evidence)
{
    private static readonly SplitBrain Clean = new(SplitBrainVerdict.NotSuspected, null);

    public bool HaltsMutation => this.Verdict != SplitBrainVerdict.NotSuspected;

    public static SplitBrain Of(VmReplicationState? onSource, VmReplicationState? onTarget)
    {
        // Two live claimants are required. One host not reporting the VM — because it is
        // unreachable, or because the VM is not there — leaves exactly one claim, which is
        // the ordinary shape of a failover rather than a conflict.
        if (onSource is null || onTarget is null)
        {
            return Clean;
        }

        if (onSource.Role == ReplicationRole.Unknown || onTarget.Role == ReplicationRole.Unknown)
        {
            return new SplitBrain(
                SplitBrainVerdict.Indeterminate,
                $"the source reports role {onSource.Role} and the target {onTarget.Role}; one "
                + "of them is a value this binary could not map, so neither claim can be judged");
        }

        // The route the replication fields cannot see: two copies powered on at once. Roles can
        // look entirely ordinary while this is true — a replica left running after a cancelled
        // failover is exactly that shape.
        //
        // Both sides must positively report Running. A peer on an older wire format sends no
        // power state at all, and an absent reading is not a reading of "off": inferring one
        // here would turn a silent peer into half a split brain.
        if (onSource.PowerState == VmPowerState.Running
            && onTarget.PowerState == VmPowerState.Running)
        {
            return new SplitBrain(
                SplitBrainVerdict.Suspected,
                "this VM is powered on and running on both hosts at once");
        }

        if (onSource.Role == ReplicationRole.Primary && onTarget.Role == ReplicationRole.Primary)
        {
            return new SplitBrain(
                SplitBrainVerdict.Suspected,
                "both hosts report this VM as primary, so both are accepting writes for it");
        }

        // The fencing case: the target is serving the VM after a failover while the original
        // primary is alive and still claiming it. Two live copies of one identity.
        if (onSource.Role == ReplicationRole.Primary
            && onTarget.State is ReplicationState.Recovered or ReplicationState.Committed)
        {
            return new SplitBrain(
                SplitBrainVerdict.Suspected,
                $"the target is serving this VM after a failover ({onTarget.State}) while the "
                + "source still reports itself primary for it");
        }

        return Clean;
    }
}
