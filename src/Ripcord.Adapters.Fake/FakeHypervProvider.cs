using Ripcord.Domain.Replication;
using Ripcord.Ports.Replication;
using Ripcord.Ports;

namespace Ripcord.Adapters.Fake;

/// Scripted host states, so every sequence the Domain decides on is exercisable without
/// Windows. The scenarios below are the ones milestone 1 has to render correctly; new ones
/// arrive with the milestone that needs them.
public sealed class FakeHypervProvider : IHypervProvider
{
    private readonly HostState local;
    private readonly HostState peer;
    private readonly Exception? localFailure;

    public FakeHypervProvider(HostState local, HostState peer)
    {
        this.local = local;
        this.peer = peer;
    }

    private FakeHypervProvider(Exception localFailure)
    {
        // Never returned — GetLocalStateAsync always faults — but a sentinel timestamp here
        // would render as a lag of two thousand years the day this fake grows a quieter
        // failure mode.
        this.local = HostState.Unreachable(
            FakeScenarios.LocalHostName, HostReachability.NotConfigured());
        this.peer = FakeScenarios.AbsentPeer();
        this.localFailure = localFailure;
    }

    /// WMI down, privileges missing, timeout on our own host: a different exit code from an
    /// unreachable peer, so the fake has to be able to produce it.
    public static FakeHypervProvider FailingLocally(string message) =>
        new(new InvalidOperationException(message));

    public Task<HostState> GetLocalStateAsync(CancellationToken cancellationToken) =>
        this.localFailure is null
            ? Task.FromResult(this.local)
            : Task.FromException<HostState>(this.localFailure);

    /// A host whose own WMI is down knows nothing about the peer either, but it does not fail
    /// on its account: the local failure is what the command reports.
    public Task<HostState> GetPeerStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(this.peer);
}

/// Named host states for the tests to compose. Keeping them here rather than in the test
/// project means the CLI can be driven by hand against a realistic pair on any machine.
public static class FakeScenarios
{
    public const string LocalHostName = "HV-REPLICA-01";
    public const string PeerHostName = "HV-PRIMARY-01";

    /// Three replicas, replicating normally, well inside the frequency.
    public static HostState Healthy(DateTimeOffset now) =>
        new(
            LocalHostName,
            [
                Replica("VM-DC-01", ReplicationHealth.Normal, now.AddSeconds(-28), 4_194_304),
                Replica("VM-LEGACY-01", ReplicationHealth.Normal, now.AddSeconds(-12), 1_048_576),
                Replica("VM-BACKUP-01", ReplicationHealth.Normal, now.AddSeconds(-31), 0),
            ],
            HostReachability.Reachable());

    /// One VM critical and resynchronising, one lagging far behind, one never replicated at
    /// all — every column of the rendering exercised at once.
    public static HostState Degraded(DateTimeOffset now) =>
        new(
            LocalHostName,
            [
                new VmReplicationState(
                    "VM-DC-01",
                    ReplicationRole.Replica,
                    ReplicationState.Resynchronizing,
                    ReplicationHealth.Critical,
                    now.AddHours(-6),
                    17_179_869_184),
                Replica("VM-LEGACY-01", ReplicationHealth.Warning, now.AddMinutes(-9), 268_435_456),
                new VmReplicationState(
                    "VM-BACKUP-01",
                    ReplicationRole.None,
                    ReplicationState.Disabled,
                    ReplicationHealth.Unknown,
                    null,
                    null),
            ],
            HostReachability.Reachable());

    /// What milestone 1 always shows for the peer, and what milestone 1b must keep showing
    /// when the other host is actually down.
    public static HostState AbsentPeer() =>
        HostState.Unreachable(PeerHostName, HostReachability.NotConfigured());

    private static VmReplicationState Replica(
        string name, ReplicationHealth health, DateTimeOffset lastReplication, long pendingBytes) =>
        new(
            name,
            ReplicationRole.Replica,
            ReplicationState.Replicating,
            health,
            lastReplication,
            pendingBytes);
}
