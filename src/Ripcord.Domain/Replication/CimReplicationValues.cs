namespace Ripcord.Domain.Replication;

/// The numeric lookups `root\virtualization\v2` reports, kept in the Domain so the WMI
/// adapter stays a translation with nothing to decide — it cannot be run off Windows, so
/// anything it decides is a decision nobody can test.
///
/// Every lookup falls back to Unknown. A value a future Windows build invents must read as
/// unknown; showing it as Replicating would be a reassuring lie.
public static class CimReplicationValues
{
    public static ReplicationState State(ushort? value) =>
        value is { } state && Enum.IsDefined((ReplicationState)state)
            ? (ReplicationState)state
            : ReplicationState.Unknown;

    public static ReplicationHealth Health(ushort? value) =>
        value is { } health && Enum.IsDefined((ReplicationHealth)health)
            ? (ReplicationHealth)health
            : ReplicationHealth.Unknown;

    /// Test replica (3) and extended replica (4) are real modes this milestone has no
    /// vocabulary for; None would claim there is no replication at all.
    public static ReplicationRole Role(ushort? value) =>
        value is { } role && Enum.IsDefined((ReplicationRole)role)
            ? (ReplicationRole)role
            : ReplicationRole.Unknown;
}
