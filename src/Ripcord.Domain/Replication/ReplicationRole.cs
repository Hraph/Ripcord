namespace Ripcord.Domain.Replication;

/// Which side of a replication relationship a VM sits on — `Msvm_ComputerSystem`'s
/// replication mode. `Get-VMReplication` calls this the mode; there is only one such axis,
/// so Ripcord names it once.
///
/// Extended replicas still map to Unknown: nothing models them yet, and showing one as an
/// ordinary replica would be worse than admitting ignorance. Test replicas arrived with
/// milestone 3, which is what creates them — leaving them Unknown would show a phantom row
/// in every table for as long as a test failover is running.
public enum ReplicationRole
{
    Unknown = -1,
    None = 0,
    Primary = 1,
    Replica = 2,
    TestReplica = 3,
}
