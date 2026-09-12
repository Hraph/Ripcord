using Ripcord.Adapters.Fake;
using Ripcord.Application.Status;
using Ripcord.Application;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;
using Ripcord.Domain.Pairing;
using Ripcord.Ports;
using Ripcord.Ports.Pairing;

namespace Ripcord.Tests.Application;

/// The use case decides one thing: which exit code the day's run produced. An unreachable
/// peer is not a failure; a local WMI failure is.
public class StatusQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_healthy_pair_renders_both_sides_and_succeeds()
    {
        StatusOutcome outcome = await Run(
            new FakeHypervProvider(FakeScenarios.Healthy(Now)));

        Assert.Equal(ExitCode.Success, outcome.Code);
        Assert.Equal(FakeScenarios.LocalHostName, outcome.View!.Local.HostName);
        Assert.Equal(3, outcome.View.Local.Vms.Count);
    }

    /// The one behaviour this milestone must get right: the peer is always unreachable, and
    /// that still exits 0. A scheduled task must not alert on it.
    [Fact]
    public async Task An_unreachable_peer_still_exits_zero()
    {
        StatusOutcome outcome = await Run(
            new FakeHypervProvider(FakeScenarios.Healthy(Now)));

        Assert.Equal(ExitCode.Success, outcome.Code);
        Assert.False(outcome.View!.Peer.IsReachable);
        Assert.Equal(FakeScenarios.PeerHostName, outcome.View.Peer.HostName);
    }

    /// A channel that throws is the same thing to the operator as a peer that says nothing —
    /// never a crash, and never a non-zero code.
    [Fact]
    public async Task A_channel_that_throws_degrades_instead_of_failing_the_command()
    {
        StatusOutcome outcome = await Run(peerChannel: new ThrowingPeerChannel());

        Assert.Equal(ExitCode.Success, outcome.Code);
        Assert.Equal(ReachabilityKind.Failed, outcome.View!.Peer.Reachability.Kind);
        Assert.Contains("peer is on fire", outcome.View.Peer.Reachability.Reason);
        Assert.Equal(FakeScenarios.PeerHostName, outcome.View.Peer.HostName);
    }

    /// The peer names itself in its snapshot, but the configuration is what this host trusts:
    /// a snapshot claiming another name must not silently relabel the pair view.
    [Fact]
    public async Task The_peer_is_named_from_the_configuration_not_from_its_own_snapshot()
    {
        HostSnapshot misnamed = new(
            Now.AddMinutes(-1),
            new HostState("WHATEVER-THE-PEER-SAID", [], HostReachability.Reachable()));

        StatusOutcome outcome = await Run(peerChannel: FakePeerChannel.Answering(misnamed));

        Assert.Equal(FakeScenarios.PeerHostName, outcome.View!.Peer.HostName);
    }

    /// The peer view is as old as the snapshot it came from, and that age is part of the
    /// answer rather than a detail.
    [Fact]
    public async Task The_snapshot_timestamp_travels_with_the_pair_view()
    {
        HostSnapshot snapshot = FakeScenarios.PeerSnapshot(Now.AddMinutes(-4));

        StatusOutcome outcome = await Run(peerChannel: FakePeerChannel.Answering(snapshot));

        Assert.Equal(Now.AddMinutes(-4), outcome.View!.PeerCapturedAt);
        Assert.True(outcome.View.Peer.IsReachable);
    }

    [Fact]
    public async Task A_silent_peer_carries_no_snapshot_timestamp()
    {
        StatusOutcome outcome = await Run(
            peerChannel: FakePeerChannel.TimingOut(Now.AddMinutes(-3)));

        Assert.Null(outcome.View!.PeerCapturedAt);
        Assert.Equal(ReachabilityKind.TimedOut, outcome.View.Peer.Reachability.Kind);
    }

    /// This host publishes its own state before reading the other one, so the peer's next
    /// `status` sees a fresh snapshot rather than one from whenever a task last ran.
    [Fact]
    public async Task Status_publishes_the_local_snapshot_before_reading_the_peer()
    {
        InMemorySnapshotStore store = new();

        await Run(snapshotStore: store);

        HostSnapshot published = Assert.Single(store.Written).Value;
        Assert.Equal(Now, published.CapturedAt);
        Assert.Equal(FakeScenarios.LocalHostName, published.State.HostName);
    }

    /// Publishing is a courtesy to the peer, not part of reading this host. A file that
    /// cannot be written must not turn a working `status` into a failure.
    [Fact]
    public async Task A_snapshot_that_cannot_be_published_does_not_fail_the_command()
    {
        StatusOutcome outcome = await Run(snapshotStore: new FailingSnapshotStore());

        Assert.Equal(ExitCode.Success, outcome.Code);
    }

    [Fact]
    public async Task A_local_access_failure_exits_three_with_no_view()
    {
        StatusOutcome outcome = await Run(FakeHypervProvider.FailingLocally("access denied"));

        Assert.Equal(ExitCode.LocalAccessFailure, outcome.Code);
        Assert.Null(outcome.View);
        Assert.Contains("access denied", outcome.FailureMessage);
    }

    [Fact]
    public async Task An_invalid_configuration_exits_two_before_touching_hyper_v()
    {
        RecordingHypervProvider provider = new(FakeScenarios.Healthy(Now));

        StatusOutcome outcome = await Run(provider, machineName: "SOME-OTHER-HOST");

        Assert.Equal(ExitCode.InvalidConfiguration, outcome.Code);
        Assert.Null(outcome.View);
        Assert.Contains("node.hostname", outcome.Errors.Select(error => error.Path));
        Assert.False(provider.WasAsked);
    }

    [Fact]
    public async Task A_configuration_that_cannot_be_read_exits_two()
    {
        StatusQuery query = new(
            new StubConfigStore(ConfigurationRead.Failed("ripcord.yaml", "file not found")),
            new FakeHypervProvider(FakeScenarios.Healthy(Now)),
            FakePeerChannel.Absent(),
            new InMemorySnapshotStore(),
            new FixedClock(Now));

        StatusOutcome outcome = await query.ExecuteAsync(
            new StatusRequest("ripcord.yaml", FakeScenarios.LocalHostName), CancellationToken.None);

        Assert.Equal(ExitCode.InvalidConfiguration, outcome.Code);
        Assert.Contains("file not found", Assert.Single(outcome.Errors).Message);
    }

    /// The rendering needs the offline threshold to say "offline" rather than "silent", so
    /// the validated configuration has to come back out with the view.
    [Fact]
    public async Task The_validated_configuration_comes_back_with_the_outcome()
    {
        StatusOutcome outcome = await Run(
            new FakeHypervProvider(FakeScenarios.Healthy(Now)));

        Assert.Equal(TimeSpan.FromSeconds(120), outcome.Configuration!.Peer.OfflineAfter);
    }

    private static Task<StatusOutcome> Run(
        IHypervProvider? provider = null,
        string machineName = FakeScenarios.LocalHostName,
        IPeerChannel? peerChannel = null,
        ISnapshotStore? snapshotStore = null)
    {
        StatusQuery query = new(
            new StubConfigStore(ConfigurationRead.Succeeded(ValidDocument())),
            provider ?? new FakeHypervProvider(FakeScenarios.Healthy(Now)),
            peerChannel ?? FakePeerChannel.Absent(),
            snapshotStore ?? new InMemorySnapshotStore(),
            new FixedClock(Now));

        return query.ExecuteAsync(
            new StatusRequest("ripcord.yaml", machineName), CancellationToken.None);
    }

    private static ConfigurationDocument ValidDocument() => new()
    {
        SchemaVersion = 1,
        Node = new NodeDocument { Hostname = FakeScenarios.LocalHostName },
        Peer = new PeerDocument
        {
            Hostname = FakeScenarios.PeerHostName,
            Address = "192.0.2.11",
            OfflineAfterSec = 120,
        },
        Listener = new ListenerDocument
        {
            Enabled = true,
            Port = 7443,
            LocalCertificateThumbprint = "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555",
            PeerCertificateThumbprint = "1111AAAA2222BBBB3333CCCC4444DDDD5555EEEE",
            SnapshotPath = "state.json",
        },
        Vms = [new VmDocument { Name = "VM-DC-01", Priority = "P1" }],
    };

    private sealed class StubConfigStore(ConfigurationRead read) : IConfigStore
    {
        public ConfigurationRead Read(string path) => read;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class ThrowingPeerChannel : IPeerChannel
    {
        public Task<PeerFetch> FetchAsync(
            PeerEndpoint endpoint, CancellationToken cancellationToken) =>
            throw new TimeoutException("the peer is on fire");
    }

    private sealed class FailingSnapshotStore : ISnapshotStore
    {
        public void Write(string path, HostSnapshot snapshot) =>
            throw new UnauthorizedAccessException("access to the snapshot file is denied");

        public HostSnapshot? Read(string path) => null;
    }

    private sealed class RecordingHypervProvider(HostState local) : IHypervProvider
    {
        public bool WasAsked { get; private set; }

        public Task<HostState> GetLocalStateAsync(CancellationToken cancellationToken)
        {
            this.WasAsked = true;
            return Task.FromResult(local);
        }
    }
}
