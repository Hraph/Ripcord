using Ripcord.Domain.Failover;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Failover;

/// Two hosts both believing they own the same VM is the state from which no failover sequence
/// recovers: both sides are writing, and whichever is discarded takes real work with it. Every
/// mutating command stops here, so the detection has to be right in both directions — a missed
/// split brain destroys data, and a phantom one blocks the failover during an incident.
public class SplitBrainTests
{
    private const string Vm = "VM-DC-01";

    /// The canonical case. After an unplanned failover the replica was reversed to primary;
    /// the original host then came back still believing it is primary too.
    [Fact]
    public void Both_sides_claiming_primary_is_suspected()
    {
        SplitBrain verdict = SplitBrain.Of(
            Claim(ReplicationRole.Primary, ReplicationState.Replicating),
            Claim(ReplicationRole.Primary, ReplicationState.Replicating));

        Assert.Equal(SplitBrainVerdict.Suspected, verdict.Verdict);
    }

    /// The other route to the same place: the target is serving the VM after a failover while
    /// the original primary is alive and still claiming it. This is what fencing exists to
    /// prevent, and it is how a second domain controller ends up live on the same subnet.
    [Fact]
    public void A_live_original_primary_beside_a_failed_over_target_is_suspected()
    {
        SplitBrain verdict = SplitBrain.Of(
            Claim(ReplicationRole.Primary, ReplicationState.Replicating),
            Claim(ReplicationRole.Replica, ReplicationState.Recovered));

        Assert.Equal(SplitBrainVerdict.Suspected, verdict.Verdict);
    }

    [Fact]
    public void A_committed_failover_beside_a_live_primary_is_also_suspected()
    {
        SplitBrain verdict = SplitBrain.Of(
            Claim(ReplicationRole.Primary, ReplicationState.Replicating),
            Claim(ReplicationRole.Replica, ReplicationState.Committed));

        Assert.Equal(SplitBrainVerdict.Suspected, verdict.Verdict);
    }

    [Fact]
    public void A_normally_replicating_pair_is_not_suspected()
    {
        SplitBrain verdict = SplitBrain.Of(
            Claim(ReplicationRole.Primary, ReplicationState.Replicating),
            Claim(ReplicationRole.Replica, ReplicationState.Replicating));

        Assert.Equal(SplitBrainVerdict.NotSuspected, verdict.Verdict);
    }

    /// A pair that has been failed over and reversed is the normal shape of running on DR.
    /// Reporting it as split brain would block `failback`, which is the command that exists to
    /// resolve it.
    [Fact]
    public void A_reversed_pair_running_on_dr_is_not_suspected()
    {
        SplitBrain verdict = SplitBrain.Of(
            Claim(ReplicationRole.Replica, ReplicationState.Replicating),
            Claim(ReplicationRole.Primary, ReplicationState.Replicating));

        Assert.Equal(SplitBrainVerdict.NotSuspected, verdict.Verdict);
    }

    /// The defining circumstance of an unplanned failover: the primary is gone. Split brain
    /// needs two live claimants, and silence is not a claim — treating it as one would refuse
    /// the failover in exactly the situation the tool exists for.
    [Fact]
    public void A_source_that_was_not_read_is_not_a_claimant()
    {
        SplitBrain verdict = SplitBrain.Of(
            null, Claim(ReplicationRole.Replica, ReplicationState.Recovered));

        Assert.Equal(SplitBrainVerdict.NotSuspected, verdict.Verdict);
    }

    /// A role this binary could not map is not evidence of safety. It is reported as its own
    /// answer so a caller can decide, rather than folded into "not suspected".
    [Fact]
    public void An_unmapped_role_is_indeterminate_rather_than_safe()
    {
        SplitBrain verdict = SplitBrain.Of(
            Claim(ReplicationRole.Unknown, ReplicationState.Replicating),
            Claim(ReplicationRole.Replica, ReplicationState.Replicating));

        Assert.Equal(SplitBrainVerdict.Indeterminate, verdict.Verdict);
    }

    /// Indeterminate on the target side counts the same. The asymmetry would be a bug.
    [Fact]
    public void An_unmapped_target_role_is_indeterminate_too()
    {
        SplitBrain verdict = SplitBrain.Of(
            Claim(ReplicationRole.Primary, ReplicationState.Replicating),
            Claim(ReplicationRole.Unknown, ReplicationState.Replicating));

        Assert.Equal(SplitBrainVerdict.Indeterminate, verdict.Verdict);
    }

    /// A suspicion nobody can act on is a refusal without a reason. The evidence is what the
    /// operator reads before deciding which side to keep.
    [Fact]
    public void A_suspicion_carries_the_evidence_for_it()
    {
        SplitBrain verdict = SplitBrain.Of(
            Claim(ReplicationRole.Primary, ReplicationState.Replicating),
            Claim(ReplicationRole.Primary, ReplicationState.Replicating));

        Assert.False(string.IsNullOrWhiteSpace(verdict.Evidence));
    }

    /// Nothing suspected means nothing to explain, and an evidence string on a clean verdict
    /// would end up printed somewhere as if it were a finding.
    [Fact]
    public void A_clean_verdict_carries_no_evidence()
    {
        SplitBrain verdict = SplitBrain.Of(
            Claim(ReplicationRole.Primary, ReplicationState.Replicating),
            Claim(ReplicationRole.Replica, ReplicationState.Replicating));

        Assert.Null(verdict.Evidence);
    }

    private static VmReplicationState Claim(ReplicationRole role, ReplicationState state) =>
        new(Vm, role, state, ReplicationHealth.Normal, null, null);
}
