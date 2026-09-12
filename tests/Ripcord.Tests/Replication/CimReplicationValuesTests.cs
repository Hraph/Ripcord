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

    /// Test replica (3) and extended replica (4) are real modes this milestone has no
    /// vocabulary for. Unknown is the honest answer; None would claim there is no replication.
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(42)]
    public void Modes_this_milestone_does_not_model_are_unknown_not_none(ushort value)
    {
        Assert.Equal(ReplicationRole.Unknown, CimReplicationValues.Role(value));
    }

    [Fact]
    public void A_missing_role_is_unknown_rather_than_none()
    {
        Assert.Equal(ReplicationRole.Unknown, CimReplicationValues.Role(null));
    }
}
