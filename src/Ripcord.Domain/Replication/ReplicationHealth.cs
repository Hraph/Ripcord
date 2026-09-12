namespace Ripcord.Domain.Replication;

/// Hyper-V's own health verdict, carried through unchanged. Ripcord does not judge it at
/// milestone 1 — that is `ripcord check`. Value 0, "not applicable", is Unknown: a VM with
/// no relationship has no health rather than a good one.
public enum ReplicationHealth
{
    Unknown = 0,
    Normal = 1,
    Warning = 2,
    Critical = 3,
}
