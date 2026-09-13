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
}
