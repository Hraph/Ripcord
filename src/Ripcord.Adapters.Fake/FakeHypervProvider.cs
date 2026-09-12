using Ripcord.Domain.Replication;
using Ripcord.Domain.Pairing;
using Ripcord.Ports.Pairing;
using Ripcord.Ports.Replication;
using Ripcord.Ports;

namespace Ripcord.Adapters.Fake;

/// Scripted host states, so every sequence the Domain decides on is exercisable without
/// Windows. The scenarios below are the ones milestone 1 has to render correctly; new ones
/// arrive with the milestone that needs them.
public sealed class FakeHypervProvider : IHypervProvider
{
    private readonly HostState local;
    private readonly Exception? localFailure;

    public FakeHypervProvider(HostState local) => this.local = local;

    private FakeHypervProvider(Exception localFailure)
    {
        // Never returned — GetLocalStateAsync always faults — but a sentinel timestamp here
        // would render as a lag of two thousand years the day this fake grows a quieter
        // failure mode.
        this.local = HostState.Unreachable(
            FakeScenarios.LocalHostName, HostReachability.NotConfigured());
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
}

/// The other host, scripted. Every reachability the rendering must tell apart has a factory
/// here, because they are what milestone 1b has to get right.
public sealed class FakePeerChannel(PeerFetch fetch) : IPeerChannel
{
    public PeerEndpoint? LastEndpoint { get; private set; }

    public static FakePeerChannel Answering(HostSnapshot snapshot) =>
        new(PeerFetch.Answered(snapshot));

    public static FakePeerChannel TimingOut(DateTimeOffset since) =>
        new(PeerFetch.Silent(HostReachability.TimedOut(since)));

    public static FakePeerChannel Refusing(DateTimeOffset since) =>
        new(PeerFetch.Silent(HostReachability.Refused(since)));

    public static FakePeerChannel Absent() =>
        new(PeerFetch.Silent(HostReachability.NotConfigured()));

    public Task<PeerFetch> FetchAsync(PeerEndpoint endpoint, CancellationToken cancellationToken)
    {
        this.LastEndpoint = endpoint;
        return Task.FromResult(fetch);
    }
}

/// The snapshot file, without a file. Records what was published so a test can assert that
/// `status` refreshed this host before reading the other one.
public sealed class InMemorySnapshotStore : ISnapshotStore
{
    private readonly Dictionary<string, HostSnapshot> written = [];

    public IReadOnlyDictionary<string, HostSnapshot> Written => this.written;

    public void Write(string path, HostSnapshot snapshot) => this.written[path] = snapshot;

    public HostSnapshot? Read(string path) => this.written.GetValueOrDefault(path);
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

    /// What the peer publishes when it is healthy.
    public static HostSnapshot PeerSnapshot(DateTimeOffset capturedAt) =>
        new(
            capturedAt,
            new HostState(
                PeerHostName,
                [
                    Primary("VM-DC-01", ReplicationHealth.Normal, capturedAt.AddSeconds(-18)),
                    Primary("VM-LEGACY-01", ReplicationHealth.Normal, capturedAt.AddSeconds(-9)),
                ],
                HostReachability.Reachable()));

    private static VmReplicationState Primary(
        string name, ReplicationHealth health, DateTimeOffset lastReplication) =>
        new(
            name,
            ReplicationRole.Primary,
            ReplicationState.Replicating,
            health,
            lastReplication,
            0);

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
