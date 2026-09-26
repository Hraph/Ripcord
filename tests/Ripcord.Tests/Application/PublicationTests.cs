using Ripcord.Adapters.Fake;
using Ripcord.Application;
using Ripcord.Domain;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;
using Ripcord.Ports.Hosts;
using Ripcord.Ports.Pairing;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Application;

/// Publishing this host's snapshot on its own — what the publishing service does every fifteen
/// seconds — says how it went, and never reads the peer.
public class PublicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    private static readonly RipcordConfiguration Configuration = Configurations.Create();

    [Fact]
    public async Task A_host_that_reads_is_published_without_asking_the_peer()
    {
        InMemorySnapshotStore store = new();
        FakePeerChannel peer = FakePeerChannel.Answering(new HostSnapshot(Now, HostState.Unreachable("x", HostReachability.NotConfigured())));

        Publication publication = await Reader(store, peer: peer).PublishAsync(Configuration, CancellationToken.None);

        Assert.Equal(PublicationKind.Published, publication.Kind);
        Assert.Equal(Now, store.Read(Configuration.Listener.SnapshotPath)!.CapturedAt);
        Assert.Null(peer.LastEndpoint);
    }

    [Fact]
    public async Task Hyper_v_that_cannot_be_read_publishes_nothing_and_says_why()
    {
        InMemorySnapshotStore store = new();

        Publication publication = await Reader(store, FakeHypervProvider.FailingLocally("WMI is down"))
            .PublishAsync(Configuration, CancellationToken.None);

        Assert.Equal(PublicationKind.NotRead, publication.Kind);
        Assert.Contains("WMI is down", publication.Reason, StringComparison.Ordinal);
        Assert.Empty(store.Written);
    }

    [Fact]
    public async Task A_file_that_cannot_be_written_is_not_written_with_its_reason()
    {
        Publication publication = await Reader(new RefusingStore())
            .PublishAsync(Configuration, CancellationToken.None);

        Assert.Equal(PublicationKind.NotWritten, publication.Kind);
        Assert.Equal("Access to the path is denied.", publication.Reason);
    }

    [Fact]
    public async Task A_disabled_listener_has_nothing_to_publish()
    {
        InMemorySnapshotStore store = new();

        Publication publication = await Reader(store).PublishAsync(
            Configurations.Create(document => document.Listener!.Enabled = false), CancellationToken.None);

        Assert.Equal(PublicationKind.ListenerDisabled, publication.Kind);
        Assert.Empty(store.Written);
    }

    /// The publishing service may not be allowed to read BitLocker; an administrator's earlier
    /// read was. The state is kept, with the date it was read, rather than published unknown.
    [Fact]
    public async Task BitLocker_this_read_cannot_see_is_carried_from_the_last_snapshot_with_its_date()
    {
        DateTimeOffset earlier = Now.AddHours(-3);
        InMemorySnapshotStore store = new();
        store.Write(
            Configuration.Listener.SnapshotPath,
            new HostSnapshot(earlier, State([new HostVolume("D:", 1, 2, true, false)])));

        await Reader(store, hostSystem: Reading(new HostVolume("D:", 5, 10, null, null)))
            .PublishAsync(Configuration, CancellationToken.None);

        HostVolume published = store.Read(Configuration.Listener.SnapshotPath)!.State.Facts!.Volume("D:")!;
        Assert.Equal(5, published.FreeBytes);
        Assert.True(published.IsBitLockerProtected);
        Assert.False(published.IsAutoUnlockEnabled);
        Assert.Equal(earlier, published.BitLockerReadAt);
    }

    [Fact]
    public async Task BitLocker_read_now_is_published_as_read_now()
    {
        InMemorySnapshotStore store = new();
        store.Write(
            Configuration.Listener.SnapshotPath,
            new HostSnapshot(Now.AddHours(-3), State([new HostVolume("D:", 1, 2, true, false)])));

        await Reader(store, hostSystem: Reading(new HostVolume("D:", 5, 10, true, true)))
            .PublishAsync(Configuration, CancellationToken.None);

        HostVolume published = store.Read(Configuration.Listener.SnapshotPath)!.State.Facts!.Volume("D:")!;
        Assert.True(published.IsAutoUnlockEnabled);
        Assert.Null(published.BitLockerReadAt);
    }

    private static HostState State(IReadOnlyList<HostVolume> volumes) =>
        new(ValidDocument.MachineName, [], HostReachability.Reachable(), new HostFacts(12_288, volumes, null));

    private static FakeHostSystemProvider Reading(params HostVolume[] volumes) =>
        new(new HostSystemReading(12_288, volumes));

    private static PairReader Reader(
        ISnapshotStore store,
        FakeHypervProvider? provider = null,
        FakeHostSystemProvider? hostSystem = null,
        FakePeerChannel? peer = null) =>
        new(
            new LocalStateReader(
                provider ?? new FakeHypervProvider(FakeScenarios.Healthy(Now)),
                hostSystem ?? FakeHostSystemProvider.Target(),
                FakeCertificateProvider.Valid(ValidDocument.LocalThumbprint, "CN=HV-REPLICA-01"),
                new SilentDiagnosticLog()),
            peer ?? FakePeerChannel.Absent(),
            store,
            new FixedClock(Now),
            new BuildIdentity("0.8.0", "abc123"),
            new SilentDiagnosticLog());

    private sealed class RefusingStore : ISnapshotStore
    {
        public void Write(string path, HostSnapshot snapshot) =>
            throw new UnauthorizedAccessException("Access to the path is denied.");

        public HostSnapshot? Read(string path) => null;
    }
}
