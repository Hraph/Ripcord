using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Replication;

/// A record holding a list compares that list by reference unless told otherwise, which makes
/// two identical host states unequal. The wire round-trip is the first thing that trips on it;
/// milestone 2's cross-host comparisons would be the next.
public class HostStateEqualityTests
{
    [Fact]
    public void Two_host_states_with_the_same_vms_are_equal()
    {
        Assert.Equal(State(Vm("VM-DC-01")), State(Vm("VM-DC-01")));
        Assert.Equal(State(Vm("VM-DC-01")).GetHashCode(), State(Vm("VM-DC-01")).GetHashCode());
    }

    [Fact]
    public void Host_states_differing_in_a_vm_are_not_equal()
    {
        Assert.NotEqual(State(Vm("VM-DC-01")), State(Vm("VM-LEGACY-01")));
    }

    [Fact]
    public void Host_states_differing_in_vm_count_are_not_equal()
    {
        Assert.NotEqual(State(Vm("VM-DC-01")), State(Vm("VM-DC-01"), Vm("VM-LEGACY-01")));
    }

    /// Order is meaningful on the wire even though the rendering sorts: two payloads that
    /// differ only in order are different payloads.
    [Fact]
    public void Host_states_differing_only_in_vm_order_are_not_equal()
    {
        Assert.NotEqual(
            State(Vm("VM-DC-01"), Vm("VM-LEGACY-01")),
            State(Vm("VM-LEGACY-01"), Vm("VM-DC-01")));
    }

    private static HostState State(params VmReplicationState[] vms) =>
        new("HV-PRIMARY-01", vms, HostReachability.Reachable());

    private static VmReplicationState Vm(string name) =>
        new(
            name,
            ReplicationRole.Primary,
            ReplicationState.Replicating,
            ReplicationHealth.Normal,
            null,
            null);
}
