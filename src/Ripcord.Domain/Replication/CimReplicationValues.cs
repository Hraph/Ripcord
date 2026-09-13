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

    /// The one lookup here that can return null, and deliberately so. The others answer a
    /// question the host always has an answer to; this one answers "is the VM running", where
    /// an absent `EnabledState` means nobody could see, and that is a third answer rather than
    /// a reading.
    ///
    /// It matters because the alternatives are opposites. A failover sequence re-derives where
    /// it has got to from whether the VM is off, and `SplitBrain` concludes a conflict from two
    /// copies being on. Folding "could not see" into either would make the sequence step over
    /// a shutdown that never happened, or invent a conflict out of a silent peer.
    public static VmPowerState? Power(ushort? value) =>
        value is not { } state
            ? null
            : Enum.IsDefined((VmPowerState)state)
                ? (VmPowerState)state
                : VmPowerState.Unknown;

    /// `Msvm_VirtualSystemSettingData.AutomaticStartupAction`. Nullable for the same reason
    /// `Power` is, and for a sharper consequence: fencing reads this to decide whether the
    /// returning host would boot the old domain controller alongside the failed-over one.
    public static AutomaticStartAction? StartAction(ushort? value) =>
        value is not { } action
            ? null
            : Enum.IsDefined((AutomaticStartAction)action)
                ? (AutomaticStartAction)action
                : AutomaticStartAction.Unknown;
}
