namespace Ripcord.Domain.Replication;

/// Which side of a replication relationship a VM sits on — `Msvm_ComputerSystem`'s
/// replication mode. `Get-VMReplication` calls this the mode; there is only one such axis,
/// so Ripcord names it once.
///
/// Test and extended replicas map to Unknown here: milestone 1 has no vocabulary for them,
/// and showing a test replica as an ordinary one would be worse than admitting ignorance.
public enum ReplicationRole
{
    Unknown = -1,
    None = 0,
    Primary = 1,
    Replica = 2,
}
