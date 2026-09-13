namespace Ripcord.Domain.Replication;

/// What a host does with a VM when the host itself boots —
/// `Msvm_VirtualSystemSettingData.AutomaticStartupAction`, named as the CIM documentation
/// names it so the adapter stays a numeric lookup with nothing to interpret.
///
/// It matters here for one reason: after an unplanned failover the original primary still
/// holds a copy of every VM that moved, and the two hosts share an external switch on one
/// subnet. Restoring power with the common `StartIfRunning` default boots the original domain
/// controller alongside the failed-over one.
///
/// There is no default value. A VM whose setting was not read carries `null`, never `Nothing`
/// — "I could not see" answering as "it will not boot" is exactly the mistake that ends with
/// two live domain controllers.
public enum AutomaticStartAction
{
    Unknown = -1,
    Nothing = 2,
    StartIfRunning = 3,
    Start = 4,
}
