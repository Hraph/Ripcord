using Ripcord.Adapters.Fake;
using Ripcord.Application.Status;
using Ripcord.Application;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;
using Ripcord.Domain.TestFailover;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Deployment;
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
            Pair(new FakeHypervProvider(FakeScenarios.Healthy(Now))),
            ListenerService.Running());

        StatusOutcome outcome = await query.ExecuteAsync(
            new StatusRequest("ripcord.yaml", FakeScenarios.LocalHostName), CancellationToken.None);

        Assert.Equal(ExitCode.InvalidConfiguration, outcome.Code);
        Assert.Contains("file not found", Assert.Single(outcome.Errors).Message);
    }

    /// The peer's `check` reasons about this host as the *target* of a failover, and the
    /// listener never reads Hyper-V (decision D18). So whatever the rules compare has to be
    /// in the snapshot `status` publishes, not fetched live.
    [Fact]
    public async Task The_published_snapshot_carries_the_host_and_vm_facts()
    {
        InMemorySnapshotStore store = new();

        await Run(snapshotStore: store);

        HostState published = Assert.Single(store.Written).Value.State;

        Assert.Equal(12_288, published.Facts!.PhysicalRamMb);
        Assert.NotNull(published.Facts.Volume("D:"));
        Assert.Equal(
            FakeScenarios.ProductionSwitch, published.Vms[0].Facts!.Adapters[0].SwitchName);
    }

    /// A degradation reaches the caller rather than being swallowed: `status` still renders,
    /// and the operator is told which facts are missing.
    [Fact]
    public async Task A_host_system_failure_is_reported_as_a_note_and_still_exits_zero()
    {
        StatusOutcome outcome = await Run(hostSystem: FakeHostSystemProvider.Failing("cimv2 down"));

        Assert.Equal(ExitCode.Success, outcome.Code);
        Assert.Contains(outcome.Notes, note => note.Contains("cimv2 down"));
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

    [Fact]
    public async Task A_running_listener_adds_no_alert()
    {
        StatusOutcome outcome = await Run();

        Assert.Null(outcome.Rendered!.Listener);
    }

    /// The other host shows this one SILENT; only this side can say the listener is down.
    [Fact]
    public async Task A_stopped_listener_is_reported_and_still_exits_zero()
    {
        StatusOutcome outcome = await Run(executor: ListenerService.In(ServiceRunState.Stopped));

        Assert.Equal(ExitCode.Success, outcome.Code);
        Assert.Equal("NOT RUNNING", outcome.Rendered!.Listener!.Headline);
    }

    [Fact]
    public async Task A_service_that_cannot_be_read_degrades_to_unknown()
    {
        StatusOutcome outcome = await Run(executor: ListenerService.Failing("RPC unavailable"));

        Assert.Equal(ExitCode.Success, outcome.Code);
        Assert.Equal("UNKNOWN", outcome.Rendered!.Listener!.Headline);
        Assert.Contains("RPC unavailable", outcome.Rendered.Listener.Reason, StringComparison.Ordinal);
    }

    private static Task<StatusOutcome> Run(
        IHypervProvider? provider = null,
        string machineName = FakeScenarios.LocalHostName,
        IPeerChannel? peerChannel = null,
        ISnapshotStore? snapshotStore = null,
        FakeHostSystemProvider? hostSystem = null,
        IDeploymentExecutor? executor = null)
    {
        StatusQuery query = new(
            new StubConfigStore(ConfigurationRead.Succeeded(ValidDocument())),
            Pair(
                provider ?? new FakeHypervProvider(FakeScenarios.Healthy(Now)),
                hostSystem,
                peerChannel,
                snapshotStore),
            executor ?? ListenerService.Running());

        return query.ExecuteAsync(
            new StatusRequest("ripcord.yaml", machineName), CancellationToken.None);
    }

    /// The shared well-formed document. The snapshot path carries a folder because a path
    /// without one is refused: it would resolve against the working directory of whoever runs
    /// the command, and a Windows service's is `system32`.
    private static ConfigurationDocument ValidDocument()
    {
        ConfigurationDocument document = Tests.Configuration.ValidDocument.Create();
        document.Listener!.SnapshotPath = @"D:\Ripcord\state.json";
        return document;
    }

    /// The host system and certificate store are fixed here: this suite is about the pair,
    /// and `LocalStateReaderTests` is where their failure modes are settled.
    private static PairReader Pair(
        IHypervProvider provider,
        FakeHostSystemProvider? hostSystem = null,
        IPeerChannel? peerChannel = null,
        ISnapshotStore? snapshotStore = null) =>
        new(
            new LocalStateReader(
                provider,
                hostSystem ?? FakeHostSystemProvider.Target(),
                FakeCertificateProvider.Valid(
                    Tests.Configuration.ValidDocument.LocalThumbprint, "CN=HV-REPLICA-01"),
                new SilentDiagnosticLog()),
            peerChannel ?? FakePeerChannel.Absent(),
            snapshotStore ?? new InMemorySnapshotStore(),
            new FixedClock(Now),
            new BuildIdentity("0.1.0", "abc123"),
            new SilentDiagnosticLog());

    private sealed class StubConfigStore(ConfigurationRead read) : ReadOnlyConfigStore
    {
        public override ConfigurationRead Read(string path) => read;
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

    private sealed class RecordingHypervProvider(HostState local) : ReadOnlyHypervProvider
    {
        public bool WasAsked { get; private set; }

        public override Task<HostState> GetLocalStateAsync(CancellationToken cancellationToken)
        {
            this.WasAsked = true;
            return Task.FromResult(local);
        }
    }
}

/// The listener service in one state, or refusing to be read.
internal sealed class ListenerService(ObservedService? service, string? failure = null)
    : IDeploymentExecutor
{
    public static ListenerService Running() => In(ServiceRunState.Running);

    public static ListenerService In(ServiceRunState state) =>
        new(new ObservedService(true, "\"C:\\Ripcord\\ripcord.exe\" serve", state, "Auto", 0, 0));

    public static ListenerService Failing(string reason) => new(null, reason);

    public ObservedDeployment Observe(DesiredDeployment desired, ObservedService observed) =>
        ObservedDeployment.Nothing;

    public ObservedService ObserveService() =>
        service ?? throw new InvalidOperationException(failure);

    public void Apply(DeploymentStep change, DesiredDeployment desired)
    {
    }
}
