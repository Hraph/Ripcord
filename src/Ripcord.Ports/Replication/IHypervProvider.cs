using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;
using Ripcord.Domain.TestFailover;

namespace Ripcord.Ports.Replication;

/// The local host's Hyper-V. Read-only until milestone 3, which adds the first mutating
/// operations — and they stay thin. The sequence they belong to, its ordering and its cleanup
/// guarantee live in the Application layer, where they can be tested against the fake; an
/// adapter that started deciding would be a decision nobody can test until someone is on
/// Windows.
///
/// The peer is not here: since decision D18 it is read from a published snapshot over
/// IPeerChannel, not from Hyper-V. An exception from this port means a local failure — WMI
/// down, privileges missing, timeout on our own host — which is its own exit code.
public interface IHypervProvider
{
    Task<HostState> GetLocalStateAsync(CancellationToken cancellationToken);

    /// Every virtual switch on this host and what it can reach. Needed because a test VM is
    /// safe on a switch that is not External, and a switch's name proves nothing about that.
    Task<IReadOnlyList<HostSwitch>> GetSwitchesAsync(CancellationToken cancellationToken);

    /// Test VMs currently on this host, discriminated on ReplicationMode = TestReplica (3).
    /// Never on the `" - Test"` name suffix, whose localisation is unverifiable.
    Task<IReadOnlyList<TestVm>> GetTestVmsAsync(CancellationToken cancellationToken);

    /// Points the replica's test-failover adapters at a switch, or at nothing when the name
    /// is null. Without this a test VM inherits the replica's production switch, which the
    /// isolation rule then refuses — correctly, and every single time.
    Task AttachTestNetworkAsync(
        string vmName, string? switchName, CancellationToken cancellationToken);

    /// Creates the test VM and returns it as Hyper-V named it. Returning the created system
    /// rather than deriving its name is what keeps the `" - Test"` suffix out of the code.
    Task<TestVm> StartTestFailoverAsync(string vmName, CancellationToken cancellationToken);

    /// Destroys the test VM. Named for the replicated VM, not for the test copy.
    Task StopTestFailoverAsync(string vmName, CancellationToken cancellationToken);

    Task StartTestVmAsync(string testVmName, CancellationToken cancellationToken);

    Task<Heartbeat> ReadHeartbeatAsync(string testVmName, CancellationToken cancellationToken);

    /// Shuts the guest down through its integration services, and fails rather than falling
    /// back to cutting the power. Step 1 of a planned failover exists so the disks stop
    /// changing in an orderly way; a forced power-off leaves the file system as it was mid
    /// write, which is the data loss the planned sequence is chosen over the unplanned one to
    /// avoid. A guest that cannot be asked politely is a refusal, not a reason to insist.
    Task ShutDownVmAsync(string vmName, CancellationToken cancellationToken);

    /// `Start-VMFailover -Prepare` on the primary: sends the last changes across and marks the
    /// failover started. Reversible — `CancelFailoverAsync` on the same host is the way back,
    /// and it is the only way back once this has run and a later step has failed.
    Task PrepareFailoverAsync(string vmName, CancellationToken cancellationToken);

    /// `Start-VMFailover` on the replica: brings the VM up on this host as the live copy.
    Task StartFailoverAsync(string vmName, CancellationToken cancellationToken);

    /// `Set-VMReplication -Reverse`: turns replication round so the old primary becomes the
    /// replica. Applied once, by the planned sequence — a failback that reverses again inverts
    /// the pair.
    Task ReverseReplicationAsync(string vmName, CancellationToken cancellationToken);

    /// Starts a real VM. Distinct from `StartTestVmAsync`, which names a test copy: the two
    /// take different names and one of them is production.
    Task StartVmAsync(string vmName, CancellationToken cancellationToken);

    /// Cancels a failover. **Which of three things this does depends on the state of the VM it
    /// is aimed at**, and one of them turns production off, so nothing calls it without
    /// `StopFailoverIntent` having resolved the effect first. The port stays thin: it invokes,
    /// it does not decide which situation it is in.
    Task CancelFailoverAsync(string vmName, CancellationToken cancellationToken);

    /// Sets what this host does with the VM on its next boot.
    ///
    /// The one mutating operation here that is not part of a failover sequence. It exists for
    /// fencing: after an unplanned failover the original primary still holds a copy of every
    /// VM that moved, and both hosts sit on the same external switch — so restoring its power
    /// with the usual `StartIfRunning` default boots the old domain controller alongside the
    /// live one. Which VMs, and back to what afterwards, is decided by `Fencing`.
    Task SetAutomaticStartActionAsync(
        string vmName, AutomaticStartAction action, CancellationToken cancellationToken);
}
