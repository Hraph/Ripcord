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

    /// Extended replica (4) is a real mode nothing models yet; None would claim there is no
    /// replication at all. Test replica (3) is modelled since milestone 3.
    public static ReplicationRole Role(ushort? value) =>
        value is { } role && Enum.IsDefined((ReplicationRole)role)
            ? (ReplicationRole)role
            : ReplicationRole.Unknown;
}
