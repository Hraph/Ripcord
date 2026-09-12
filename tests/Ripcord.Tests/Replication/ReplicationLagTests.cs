using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Replication;

/// Lag is the number the operator reads first, and the only derived value in the milestone 1
/// domain. Every edge case here is one an incident can actually produce.
public class ReplicationLagTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Lag_is_the_time_elapsed_since_the_last_replication()
    {
        VmReplicationState vm = Replicating(Now.AddMinutes(-5));

        Assert.Equal(TimeSpan.FromMinutes(5), vm.LagAt(Now));
    }

    /// A VM with no relationship, or one that has never completed a cycle, has no lag —
    /// which is not the same as a lag of zero and must not render as "0s".
    [Fact]
    public void Lag_is_unknown_when_the_vm_never_replicated()
    {
        Assert.Null(Replicating(null).LagAt(Now));
    }

    /// The two hosts' clocks drift. A future timestamp is skew, not negative lag.
    [Fact]
    public void Lag_never_goes_negative_when_the_peer_clock_runs_ahead()
    {
        Assert.Equal(TimeSpan.Zero, Replicating(Now.AddMinutes(3))!.LagAt(Now));
    }

    /// Timestamps arrive from WMI in local time; comparing instants must not depend on it.
    [Fact]
    public void Lag_is_computed_across_offsets()
    {
        DateTimeOffset sameInstantElsewhere = Now.AddMinutes(-5).ToOffset(TimeSpan.FromHours(2));

        Assert.Equal(TimeSpan.FromMinutes(5), Replicating(sameInstantElsewhere).LagAt(Now));
    }

    private static VmReplicationState Replicating(DateTimeOffset? lastReplication) =>
        new(
            "VM-DC-01",
            ReplicationRole.Replica,
            ReplicationState.Replicating,
            ReplicationHealth.Normal,
            lastReplication,
            PendingBytes: 0);
}
