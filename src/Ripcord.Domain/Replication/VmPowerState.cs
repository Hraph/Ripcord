namespace Ripcord.Domain.Replication;

/// The values `Msvm_ComputerSystem.EnabledState` reports, named as the CIM documentation names
/// them so the adapter stays a numeric lookup with nothing to interpret. The two above 32767
/// are Hyper-V's own additions to the CIM range.
///
/// There is no default: a VM whose power state was not read carries `null`, never `Off`. The
/// difference decides whether a failover step has already run, and "I could not see" answering
/// as "it is switched off" would step over the shutdown that never happened.
public enum VmPowerState
{
    Unknown = -1,
    Other = 1,
    Running = 2,
    Off = 3,
    ShuttingDown = 4,
    NotApplicable = 5,
    EnabledButOffline = 6,
    InTest = 7,
    Deferred = 8,
    Quiesce = 9,
    Starting = 10,
    Paused = 32_768,
    Saved = 32_769,
}
