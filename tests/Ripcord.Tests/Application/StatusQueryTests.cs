using Ripcord.Adapters.Fake;
using Ripcord.Application.Status;
using Ripcord.Application;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;
using Ripcord.Ports;

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
            new FakeHypervProvider(FakeScenarios.Healthy(Now), FakeScenarios.AbsentPeer()));

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
            new FakeHypervProvider(FakeScenarios.Healthy(Now), FakeScenarios.AbsentPeer()));

        Assert.Equal(ExitCode.Success, outcome.Code);
        Assert.False(outcome.View!.Peer.IsReachable);
        Assert.Equal(FakeScenarios.PeerHostName, outcome.View.Peer.HostName);
    }

    /// A peer that throws is the same thing to the operator as a peer that answers
    /// "unreachable" — never a crash, and never a non-zero code.
    [Fact]
    public async Task A_peer_that_throws_degrades_instead_of_failing_the_command()
    {
        StatusOutcome outcome = await Run(new ThrowingPeerProvider(FakeScenarios.Healthy(Now)));

        Assert.Equal(ExitCode.Success, outcome.Code);
        Assert.Equal(ReachabilityKind.Failed, outcome.View!.Peer.Reachability.Kind);
        Assert.Contains("peer is on fire", outcome.View.Peer.Reachability.Reason);
    }

    /// The peer's host name comes from the configuration, not from the peer: a host that
    /// never answered cannot tell us what it is called.
    [Fact]
    public async Task A_degraded_peer_is_still_named_from_the_configuration()
    {
        StatusOutcome outcome = await Run(new ThrowingPeerProvider(FakeScenarios.Healthy(Now)));

        Assert.Equal(FakeScenarios.PeerHostName, outcome.View!.Peer.HostName);
    }

    /// The provider cannot know what the peer is called — milestone 1 never contacts it, and
    /// milestone 1b only learns the name from a host that answered. An unreachable peer is
    /// always named from the configuration.
    [Fact]
    public async Task An_unreachable_peer_is_named_from_the_configuration_not_from_the_provider()
    {
        HostState misnamed = HostState.Unreachable(
            "WHATEVER-THE-ADAPTER-SAID", HostReachability.NotConfigured());

        StatusOutcome outcome = await Run(
            new FakeHypervProvider(FakeScenarios.Healthy(Now), misnamed));

        Assert.Equal(FakeScenarios.PeerHostName, outcome.View!.Peer.HostName);
        Assert.Equal(ReachabilityKind.NotConfigured, outcome.View.Peer.Reachability.Kind);
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
        ThrowingPeerProvider provider = new(FakeScenarios.Healthy(Now));

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
            new FakeHypervProvider(FakeScenarios.Healthy(Now), FakeScenarios.AbsentPeer()),
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
            new FakeHypervProvider(FakeScenarios.Healthy(Now), FakeScenarios.AbsentPeer()));

        Assert.Equal(TimeSpan.FromSeconds(120), outcome.Configuration!.Peer.OfflineAfter);
    }

    private static Task<StatusOutcome> Run(
        IHypervProvider provider, string machineName = FakeScenarios.LocalHostName)
    {
        StatusQuery query = new(
            new StubConfigStore(ConfigurationRead.Succeeded(ValidDocument())),
            provider,
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

    private sealed class ThrowingPeerProvider(HostState local) : IHypervProvider
    {
        public bool WasAsked { get; private set; }

        public Task<HostState> GetLocalStateAsync(CancellationToken cancellationToken)
        {
            this.WasAsked = true;
            return Task.FromResult(local);
        }

        public Task<HostState> GetPeerStateAsync(CancellationToken cancellationToken)
        {
            this.WasAsked = true;
            throw new TimeoutException("the peer is on fire");
        }
    }
}
