using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Replication;

/// The numeric CIM lookups live here rather than in the WMI adapter, because the adapter
/// cannot be run off Windows and anything it decides is a decision nobody can test.
public class CimReplicationValuesTests
{
    [Theory]
    [InlineData(0, ReplicationState.Disabled)]
    [InlineData(3, ReplicationState.Replicating)]
    [InlineData(10, ReplicationState.Resynchronizing)]
    [InlineData(14, ReplicationState.FailbackComplete)]
    public void Known_state_values_map_to_their_documented_meaning(
        ushort value, ReplicationState expected)
    {
        Assert.Equal(expected, CimReplicationValues.State(value));
    }

    /// A value a future Windows build invents must read as unknown. Showing it as Replicating
    /// would be a reassuring lie, which is the one failure mode this tool cannot afford.
    /// Values 15 to 21 arrived with Windows 10 1703 and are present on both target servers,
    /// so a 0-14 mapping would be silently wrong.
    [Theory]
    [InlineData(15, ReplicationState.DiskUpdateInProgress)]
    [InlineData(21, ReplicationState.FiredrillInProgress)]
    public void The_states_added_after_windows_8_are_mapped_too(
        ushort value, ReplicationState expected)
    {
        Assert.Equal(expected, CimReplicationValues.State(value));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(22)]
    public void An_unmapped_state_value_is_unknown(ushort value)
    {
        Assert.Equal(ReplicationState.Unknown, CimReplicationValues.State(value));
    }

    [Fact]
    public void A_missing_state_is_unknown_rather_than_disabled()
    {
        Assert.Equal(ReplicationState.Unknown, CimReplicationValues.State(null));
    }

    [Theory]
    [InlineData(0, ReplicationHealth.Unknown)]
    [InlineData(1, ReplicationHealth.Normal)]
    [InlineData(2, ReplicationHealth.Warning)]
    [InlineData(3, ReplicationHealth.Critical)]
    [InlineData(7, ReplicationHealth.Unknown)]
    public void Health_values_map_to_their_documented_meaning(
        ushort value, ReplicationHealth expected)
    {
        Assert.Equal(expected, CimReplicationValues.Health(value));
    }

    [Theory]
    [InlineData(0, ReplicationRole.None)]
    [InlineData(1, ReplicationRole.Primary)]
    [InlineData(2, ReplicationRole.Replica)]
    public void Role_values_map_to_their_documented_meaning(ushort value, ReplicationRole expected)
    {
        Assert.Equal(expected, CimReplicationValues.Role(value));
    }

    /// Extended replica (4) is a real mode nothing models yet. Unknown is the honest
    /// answer; None would claim there is no replication.
    [Theory]
    [InlineData(4)]
    [InlineData(42)]
    public void Modes_nothing_models_yet_are_unknown_not_none(ushort value)
    {
        Assert.Equal(ReplicationRole.Unknown, CimReplicationValues.Role(value));
    }

    /// Milestone 3 creates test replicas, so it also names them. Left as Unknown they would
    /// show as a phantom row for as long as a test failover ran.
    [Fact]
    public void A_test_replica_is_named_rather_than_unknown()
    {
        Assert.Equal(ReplicationRole.TestReplica, CimReplicationValues.Role(3));
    }

    [Fact]
    public void A_missing_role_is_unknown_rather_than_none()
    {
        Assert.Equal(ReplicationRole.Unknown, CimReplicationValues.Role(null));
    }

    [Theory]
    [InlineData(2, VmPowerState.Running)]
    [InlineData(3, VmPowerState.Off)]
    [InlineData(32_768, VmPowerState.Paused)]
    [InlineData(32_769, VmPowerState.Saved)]
    public void The_documented_enabled_states_are_named(ushort value, VmPowerState expected)
    {
        Assert.Equal(expected, CimReplicationValues.Power(value));
    }

    /// The only lookup here that returns null, and the distinction it draws is load-bearing.
    /// A failover sequence re-derives its position from whether the VM is off, so "nobody
    /// could see" answering as "it is off" would step over a shutdown that never happened.
    [Fact]
    public void A_missing_enabled_state_is_absent_rather_than_off()
    {
        Assert.Null(CimReplicationValues.Power(null));
    }

    /// Present but unrecognised is a reading, just not one this binary understands — so it is
    /// Unknown rather than null, and still never Off.
    [Fact]
    public void An_unrecognised_enabled_state_is_unknown_rather_than_absent_or_off()
    {
        Assert.Equal(VmPowerState.Unknown, CimReplicationValues.Power(4242));
    }

    [Theory]
    [InlineData(2, AutomaticStartAction.Nothing)]
    [InlineData(3, AutomaticStartAction.StartIfRunning)]
    [InlineData(4, AutomaticStartAction.Start)]
    public void The_documented_startup_actions_are_named(
        ushort value, AutomaticStartAction expected)
    {
        Assert.Equal(expected, CimReplicationValues.StartAction(value));
    }

    /// The same distinction the power state draws, and for a sharper reason: fencing reads
    /// this to decide whether the returning host would boot the old domain controller, and
    /// "nobody could see" answering as Nothing is how two live copies happen.
    [Fact]
    public void A_missing_startup_action_is_absent_rather_than_nothing()
    {
        Assert.Null(CimReplicationValues.StartAction(null));
    }

    [Fact]
    public void An_unrecognised_startup_action_is_unknown_rather_than_nothing()
    {
        Assert.Equal(AutomaticStartAction.Unknown, CimReplicationValues.StartAction(42));
    }
}
