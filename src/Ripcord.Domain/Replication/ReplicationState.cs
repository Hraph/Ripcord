namespace Ripcord.Domain.Replication;

/// The values `Msvm_ComputerSystem.ReplicationState` reports, named as they are in the CIM
/// documentation so the adapter stays a numeric lookup with nothing to interpret.
/// Unknown covers a value this binary does not recognise: an unmapped state must show as
/// unknown, never silently as something reassuring.
public enum ReplicationState
{
    Unknown = -1,
    Disabled = 0,
    ReadyForReplication = 1,
    WaitingToCompleteInitialReplication = 2,
    Replicating = 3,
    SyncedReplicationComplete = 4,
    Recovered = 5,
    Committed = 6,
    Suspended = 7,
    Critical = 8,
    WaitingToStartResynchronization = 9,
    Resynchronizing = 10,
    ResynchronizationSuspended = 11,
    FailoverInProgress = 12,
    FailbackInProgress = 13,
    FailbackComplete = 14,

    // Added in Windows 10 1703; present on Windows Server 2016 and 2022.
    DiskUpdateInProgress = 15,
    DiskUpdateCritical = 16,
    Undefined = 17,
    RepurposeReplicationInProgress = 18,
    PreparedForSyncReplication = 19,
    PreparedForGroupReverseReplication = 20,
    FiredrillInProgress = 21,
}
