using Ripcord.Domain.Failover;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Failover;

/// Where a sequence has got to, re-derived from what the two hosts report rather than from a
/// cursor written down earlier. A stored position survives a crash by lying about it; observed
/// state cannot. It also means an operator who runs the command on the wrong host, or twice,
/// is told where things actually stand instead of advancing a counter.
public class FailoverProgressTests
{
    private const string Vm = "VM-DC-01";
    private const string Primary = "HV-PRIMARY-01";
    private const string Replica = "HV-REPLICA-01";

    private static readonly FailoverPlan Plan = FailoverPlan.Planned(Vm, Primary, Replica);

    [Fact]
    public void A_pair_that_has_not_started_begins_at_step_one()
    {
        FailoverProgress progress = FailoverProgress.Of(
            Plan,
            Source(ReplicationRole.Primary, ReplicationState.Replicating, VmPowerState.Running),
            Target(ReplicationRole.Replica, ReplicationState.Replicating, VmPowerState.Off));

        Assert.Equal(1, progress.NextStep!.Number);
        Assert.True(progress.CanResume);
    }

    /// The VM is off on the primary, so the shutdown has happened. Nothing else has.
    [Fact]
    public void A_shut_down_vm_moves_the_sequence_to_the_prepare()
    {
        FailoverProgress progress = FailoverProgress.Of(
            Plan,
            Source(ReplicationRole.Primary, ReplicationState.Replicating, VmPowerState.Off),
            Target(ReplicationRole.Replica, ReplicationState.Replicating, VmPowerState.Off));

        Assert.Equal(2, progress.NextStep!.Number);
    }

    /// The target is serving the VM, so the failover itself has run — and therefore so did
    /// everything before it, whether or not each one left a separate trace.
    [Fact]
    public void A_recovered_target_implies_the_steps_before_it()
    {
        FailoverProgress progress = FailoverProgress.Of(
            Plan,
            Source(ReplicationRole.Primary, ReplicationState.Recovered, VmPowerState.Off),
            Target(ReplicationRole.Replica, ReplicationState.Recovered, VmPowerState.Off));

        Assert.Equal(4, progress.NextStep!.Number);
        Assert.Equal(StepState.Done, progress.Steps[1].State);
    }

    /// Replication has been turned round: the target now owns the VM.
    [Fact]
    public void A_reversed_pair_moves_on_to_starting_the_vm()
    {
        FailoverProgress progress = FailoverProgress.Of(
            Plan,
            Source(ReplicationRole.Replica, ReplicationState.Replicating, VmPowerState.Off),
            Target(ReplicationRole.Primary, ReplicationState.Recovered, VmPowerState.Off));

        Assert.Equal(5, progress.NextStep!.Number);
    }

    [Fact]
    public void A_running_vm_on_the_target_leaves_only_the_network_check()
    {
        FailoverProgress progress = FailoverProgress.Of(
            Plan,
            Source(ReplicationRole.Replica, ReplicationState.Replicating, VmPowerState.Off),
            Target(ReplicationRole.Primary, ReplicationState.Recovered, VmPowerState.Running));

        Assert.Equal(6, progress.NextStep!.Number);
    }

    /// The power state is the evidence that the shutdown ran. Without it the sequence does not
    /// know whether step 1 is behind it, and a sequence that guesses would either shut down a
    /// VM twice or step over a shutdown that never happened.
    [Fact]
    public void An_unreadable_power_state_stops_the_sequence_resuming()
    {
        FailoverProgress progress = FailoverProgress.Of(
            Plan,
            Source(ReplicationRole.Primary, ReplicationState.Replicating, null),
            Target(ReplicationRole.Replica, ReplicationState.Replicating, VmPowerState.Off));

        Assert.False(progress.CanResume);
        Assert.Equal(StepState.Indeterminate, progress.Steps[0].State);
    }

    /// A host that did not report the VM at all cannot place the sequence either.
    [Fact]
    public void An_absent_source_stops_the_sequence_resuming()
    {
        FailoverProgress progress = FailoverProgress.Of(
            Plan, null, Target(ReplicationRole.Replica, ReplicationState.Replicating, VmPowerState.Off));

        Assert.False(progress.CanResume);
    }

    /// Evidence from further along outweighs an unreadable earlier step: if the target is
    /// already serving the VM, whether the primary's power state can be read no longer decides
    /// anything. Refusing here would block a resume that is perfectly well determined.
    [Fact]
    public void Later_evidence_settles_an_earlier_step_that_could_not_be_read()
    {
        FailoverProgress progress = FailoverProgress.Of(
            Plan,
            Source(ReplicationRole.Primary, ReplicationState.Recovered, null),
            Target(ReplicationRole.Replica, ReplicationState.Recovered, VmPowerState.Off));

        Assert.True(progress.CanResume);
        Assert.Equal(StepState.Done, progress.Steps[0].State);
    }

    /// Everything observable has happened. The last step is a verification rather than a
    /// change, so the sequence still has work to do and does not report itself finished.
    [Fact]
    public void The_network_check_is_never_inferred_from_state()
    {
        FailoverProgress progress = FailoverProgress.Of(
            Plan,
            Source(ReplicationRole.Replica, ReplicationState.Replicating, VmPowerState.Off),
            Target(ReplicationRole.Primary, ReplicationState.Recovered, VmPowerState.Running));

        Assert.Equal(StepState.NotDone, progress.Steps[5].State);
        Assert.NotNull(progress.NextStep);
    }

    /// Every progress carries a sentence the report prints. "Cannot resume" without a reason
    /// is the refusal-without-because problem in another costume.
    [Fact]
    public void Progress_explains_itself()
    {
        FailoverProgress progress = FailoverProgress.Of(
            Plan,
            Source(ReplicationRole.Primary, ReplicationState.Replicating, null),
            Target(ReplicationRole.Replica, ReplicationState.Replicating, VmPowerState.Off));

        Assert.False(string.IsNullOrWhiteSpace(progress.Explanation));
    }

    private static VmReplicationState Source(
        ReplicationRole role, ReplicationState state, VmPowerState? power) =>
        new(Vm, role, state, ReplicationHealth.Normal, null, null, null, power);

    private static VmReplicationState Target(
        ReplicationRole role, ReplicationState state, VmPowerState? power) =>
        new(Vm, role, state, ReplicationHealth.Normal, null, null, null, power);
}
