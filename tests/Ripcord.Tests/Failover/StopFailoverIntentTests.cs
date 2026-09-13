using Ripcord.Domain.Failover;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Failover;

/// `Stop-VMFailover` has three behaviours and no parameter that selects between them. One of
/// the three turns production off. Ripcord therefore has to work out which one it is about to
/// invoke from what the two hosts report, and refuse when it cannot tell — the whole reason
/// this type exists is that the operator cannot tell either.
public class StopFailoverIntentTests
{
    private const string Vm = "VM-DC-01";

    /// The benign one milestone 3 already relies on. A test VM is a copy; destroying it costs
    /// nothing that was not made to be destroyed.
    [Fact]
    public void A_test_failover_in_progress_resolves_to_deleting_the_test_copy()
    {
        StopFailoverIntent intent = StopFailoverIntent.Resolve(
            OnTarget(ReplicationRole.Replica, ReplicationState.FiredrillInProgress),
            OnSource(ReplicationRole.Primary, ReplicationState.Replicating));

        Assert.Equal(StopFailoverEffect.DeletesTestCopy, intent.Effect);
        Assert.False(intent.IsDestructive);
    }

    /// A replica whose role says TestReplica is the same situation named by the other field.
    /// Either signal alone is enough; requiring both would refuse a real test failover.
    [Fact]
    public void A_test_replica_role_resolves_the_same_way()
    {
        StopFailoverIntent intent = StopFailoverIntent.Resolve(
            OnTarget(ReplicationRole.TestReplica, ReplicationState.Replicating),
            OnSource(ReplicationRole.Primary, ReplicationState.Replicating));

        Assert.Equal(StopFailoverEffect.DeletesTestCopy, intent.Effect);
    }

    /// The dangerous one. The replica has been failed over for real and is serving production;
    /// stopping here turns it off and discards the failover.
    [Fact]
    public void A_replica_in_a_real_failover_resolves_to_a_destructive_cancel()
    {
        StopFailoverIntent intent = StopFailoverIntent.Resolve(
            OnTarget(ReplicationRole.Replica, ReplicationState.Recovered),
            OnSource(ReplicationRole.Primary, ReplicationState.Replicating));

        Assert.Equal(StopFailoverEffect.CancelsRealFailover, intent.Effect);
        Assert.True(intent.IsDestructive);
    }

    /// Two signals of a real failover at once, which is still one situation and must not read
    /// as a conflict.
    [Fact]
    public void A_committed_failover_is_also_a_destructive_cancel()
    {
        StopFailoverIntent intent = StopFailoverIntent.Resolve(
            OnTarget(ReplicationRole.Replica, ReplicationState.Committed),
            OnSource(ReplicationRole.Primary, ReplicationState.Replicating));

        Assert.Equal(StopFailoverEffect.CancelsRealFailover, intent.Effect);
        Assert.True(intent.IsDestructive);
    }

    /// The same VM cannot be both a test copy and a real failover. Two contradictory readings
    /// are exactly when guessing costs the most, so nothing is resolved.
    [Fact]
    public void A_test_copy_and_a_real_failover_at_once_is_refused()
    {
        StopFailoverIntent intent = StopFailoverIntent.Resolve(
            OnTarget(ReplicationRole.TestReplica, ReplicationState.Recovered),
            OnSource(ReplicationRole.Primary, ReplicationState.Replicating));

        Assert.Null(intent.Effect);
        Assert.True(intent.IsRefused);
    }

    /// A state this binary does not recognise must not fall through to the benign reading.
    /// Unknown is the value the adapter reports for a number it could not map, and the whole
    /// point of carrying it is that it stops here.
    [Fact]
    public void An_unrecognised_state_is_refused_rather_than_assumed_benign()
    {
        StopFailoverIntent intent = StopFailoverIntent.Resolve(
            OnTarget(ReplicationRole.Replica, ReplicationState.Unknown),
            OnSource(ReplicationRole.Primary, ReplicationState.Replicating));

        Assert.Null(intent.Effect);
        Assert.True(intent.IsRefused);
    }

    /// The target could not be read at all. Absence of a reading is not a reading of absence,
    /// and it is never grounds for invoking an operation with a destructive branch.
    [Fact]
    public void An_unreadable_target_is_refused()
    {
        StopFailoverIntent intent = StopFailoverIntent.Resolve(
            null, OnSource(ReplicationRole.Primary, ReplicationState.Replicating));

        Assert.Null(intent.Effect);
        Assert.True(intent.IsRefused);
    }

    /// Nothing is happening: replication is running normally on both sides. There is no
    /// failover to stop, and saying so is different from being unable to tell.
    [Fact]
    public void A_healthy_pair_has_nothing_to_stop()
    {
        StopFailoverIntent intent = StopFailoverIntent.Resolve(
            OnTarget(ReplicationRole.Replica, ReplicationState.Replicating),
            OnSource(ReplicationRole.Primary, ReplicationState.Replicating));

        Assert.Equal(StopFailoverEffect.NothingToStop, intent.Effect);
        Assert.False(intent.IsDestructive);
    }

    /// Every refusal has to say what was seen, or the operator is left with "no" and no way
    /// to act on it. This is the field the report prints.
    [Fact]
    public void A_refusal_names_what_it_observed()
    {
        StopFailoverIntent intent = StopFailoverIntent.Resolve(
            OnTarget(ReplicationRole.Replica, ReplicationState.Unknown), null);

        Assert.False(string.IsNullOrWhiteSpace(intent.Observed));
    }

    private static VmReplicationState OnTarget(ReplicationRole role, ReplicationState state) =>
        new(Vm, role, state, ReplicationHealth.Normal, null, null);

    private static VmReplicationState OnSource(ReplicationRole role, ReplicationState state) =>
        new(Vm, role, state, ReplicationHealth.Normal, null, null);
}
