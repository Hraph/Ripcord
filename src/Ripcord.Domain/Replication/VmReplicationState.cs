using Ripcord.Domain.Inventory;

namespace Ripcord.Domain.Replication;

/// One VM as the host sees it. A VM with no relationship is still listed, with role None —
/// an unreplicated VM on the primary is exactly what the operator needs to notice.
public sealed record VmReplicationState(
    string Name,
    ReplicationRole Role,
    ReplicationState State,
    ReplicationHealth Health,
    DateTimeOffset? LastReplicationTime,
    long? PendingBytes,
    VmFacts? Facts = null,
    VmPowerState? PowerState = null,

    /// What this host would do with the VM on its next boot. Null means not read.
    AutomaticStartAction? StartAction = null)
{
    /// Null means "never replicated", which must not render as a lag of zero.
    public TimeSpan? LagAt(DateTimeOffset now) => Elapsed.Between(LastReplicationTime, now);
}
