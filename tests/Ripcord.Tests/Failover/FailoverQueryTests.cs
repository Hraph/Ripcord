using Ripcord.Adapters.Fake;
using Ripcord.Application;
using Ripcord.Application.Failover;
using Ripcord.Domain;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Failover;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Failover;

/// The composition: read the pair, judge it, then drive this host's half. `check` is the
/// precondition, so it is run rather than reimplemented — and the refusals asserted here are
/// the ones that mean the command was asked at the wrong time or of the wrong host.
public class FailoverQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    private static readonly BuildIdentity Build = new("0.4.0", "abc123def456");

    [Fact]
    public async Task An_unknown_vm_is_refused_rather_than_failing_over_nothing()
    {
        FailoverOutcome outcome = await Run("VM-TYPO-01");

        Assert.Equal(ExitCode.InvalidConfiguration, outcome.Code);
        Assert.Contains(outcome.Errors, error => error.Path == "--vm");
    }

    /// The pair runs two binaries, so a sequence spanning both would be executed half by each.
    /// It refuses before it looks at anything else about the VM.
    [Fact]
    public async Task A_version_mismatch_refuses_before_anything_else_is_judged()
    {
        FailoverOutcome outcome = await Run(
            "VM-DC-01", peerBuild: new BuildIdentity("0.3.0", "999999999999"));

        Assert.Equal(ExitCode.Refused, outcome.Code);
        Assert.Contains("0.3.0", outcome.FailureMessage!, StringComparison.Ordinal);
    }

    /// "Could not be established" must never read as "the same".
    ///
    /// Asserting the exit code alone would not do: this host has no pending step either, so it
    /// would refuse anyway and the test would pass with the version gate deleted. Mutation
    /// testing caught that. The message is what ties the refusal to the build.
    [Fact]
    public async Task A_peer_that_published_no_build_is_refused()
    {
        FailoverOutcome outcome = await Run("VM-DC-01", peerPublishesBuild: false);

        Assert.Equal(ExitCode.Refused, outcome.Code);
        Assert.Contains(
            "not running the same Ripcord",
            outcome.FailureMessage ?? "",
            StringComparison.Ordinal);
    }

    /// The precondition refused, so nothing ran — and the refusal carries the findings that
    /// caused it, because "refused" without "because" is a refusal nobody can act on.
    [Fact]
    public async Task A_precondition_refusal_carries_its_findings_and_runs_nothing()
    {
        FakeHypervProvider provider = new(FakeScenarios.Healthy(Now));

        // No peer snapshot at all: the cross-host rules cannot be evaluated, and several of
        // them block a planned failover.
        FailoverOutcome outcome = await Run(
            "VM-DC-01", provider: provider, peer: FakePeerChannel.Absent());

        Assert.Equal(ExitCode.Refused, outcome.Code);
        Assert.Empty(provider.Calls);
    }

    /// The other host reads this one only through the snapshot, so a run that acted and did not
    /// republish would leave the peer looking at the state from before it ran — and the peer is
    /// where the other half of the sequence observes what this half did.
    [Fact]
    public async Task The_snapshot_is_republished_after_the_run()
    {
        InMemorySnapshotStore snapshots = new();

        await Run("VM-DC-01", snapshots: snapshots);

        Assert.NotEmpty(snapshots.Written);
    }

    /// Running on the host that owns the first steps actually drives them. Without this the
    /// suite could pass with a query that refuses everything.
    [Fact]
    public async Task On_the_host_that_owns_the_next_steps_the_provider_is_driven()
    {
        FakeHypervProvider provider = new(Primary());

        FailoverOutcome outcome = await Run(
            "VM-DC-01",
            provider: provider,
            machineName: FakeScenarios.PeerHostName,
            expectedRole: "primary",
            peer: Replicas());

        Assert.Equal(ExitCode.Success, outcome.Code);
        Assert.NotEmpty(provider.Calls);
    }

    /// `--dry-run` reaches the same decision path and still touches nothing.
    [Fact]
    public async Task A_dry_run_reaches_the_plan_without_touching_the_host()
    {
        FakeHypervProvider provider = new(Primary());

        FailoverOutcome outcome = await Run(
            "VM-DC-01",
            provider: provider,
            machineName: FakeScenarios.PeerHostName,
            expectedRole: "primary",
            dryRun: true,
            peer: Replicas());

        Assert.Empty(provider.Calls);
        Assert.NotNull(outcome.Report);
    }

    /// The scenario the whole milestone exists for. The peer is gone, so nothing about it can
    /// be read — and the run still has to happen, on this host, with what is here.
    [Fact]
    public async Task An_unplanned_failover_proceeds_with_no_peer_at_all()
    {
        FakeHypervProvider provider = new(FakeScenarios.Healthy(Now));

        FailoverOutcome outcome = await Run(
            "VM-DC-01",
            provider: provider,
            peer: FakePeerChannel.Absent(),
            scenario: FailoverOperation.UnplannedFailover);

        Assert.Equal(ExitCode.Success, outcome.Code);
        Assert.NotEmpty(provider.Calls);
    }

    /// The version gate is what the planned path refuses on when the peer says nothing. Here
    /// it must not fire: the whole plan runs on this host, so there is no sequence for two
    /// binaries to execute half of each — and refusing for want of a version string would fail
    /// at the one thing the tool exists for.
    [Fact]
    public async Task An_unplanned_failover_is_not_refused_for_an_uncomparable_version()
    {
        FailoverOutcome outcome = await Run(
            "VM-DC-01",
            peerPublishesBuild: false,
            scenario: FailoverOperation.UnplannedFailover);

        Assert.NotEqual(ExitCode.Refused, outcome.Code);
    }

    /// The same absent peer that refuses a planned failover — several cross-host rules cannot
    /// be evaluated — must not refuse an unplanned one. Every unknown there is an unknown about
    /// a machine nobody can consult, and refusing leaves production down to protect it from
    /// booting imperfectly.
    [Fact]
    public async Task An_unplanned_failover_proceeds_past_the_unknowns_a_planned_one_stops_for()
    {
        FailoverOutcome outcome = await Run(
            "VM-DC-01",
            peer: FakePeerChannel.Absent(),
            scenario: FailoverOperation.UnplannedFailover);

        Assert.NotEqual(ExitCode.Refused, outcome.Code);
        Assert.NotEmpty(outcome.Refusal!.Proceeded);
    }

    /// And the plan it builds is the unplanned one — three steps on this host, nothing asked
    /// of the host that is gone.
    [Fact]
    public async Task An_unplanned_failover_builds_the_unplanned_plan()
    {
        FailoverOutcome outcome = await Run(
            "VM-DC-01",
            dryRun: true,
            peer: FakePeerChannel.Absent(),
            scenario: FailoverOperation.UnplannedFailover);

        Assert.Equal(FailoverOperation.UnplannedFailover, outcome.Plan!.Operation);
        Assert.Equal(3, outcome.Plan.Steps.Count);
    }

    /// Seen from the primary, the peer is the side holding the replicas. Answering with the
    /// primary's own scenario would make the progress read steps 1 to 4 as already done.
    private static FakePeerChannel Replicas() =>
        FakePeerChannel.Answering(
            new HostSnapshot(Now, FakeScenarios.Healthy(Now), Build));

    /// This host holds the primary copies, so the plan's first steps are its own.
    private static HostState Primary() =>
        new(
            FakeScenarios.PeerHostName,
            [.. FakeScenarios.PeerSnapshot(Now, Build).State.Vms],
            HostReachability.Reachable(),
            FakeScenarios.PeerSnapshot(Now, Build).State.Facts);

    private static Task<FailoverOutcome> Run(
        string vmName,
        FakeHypervProvider? provider = null,
        FakePeerChannel? peer = null,
        InMemorySnapshotStore? snapshots = null,
        BuildIdentity? peerBuild = null,

        // Distinct from `peerBuild: null`, which the null-coalescing default cannot tell from
        // "not specified" — a peer that published nothing is the case under test, not an
        // omission in the fixture.
        bool peerPublishesBuild = true,
        string machineName = FakeScenarios.LocalHostName,
        string expectedRole = "replica",
        bool dryRun = false,
        FailoverOperation scenario = FailoverOperation.PlannedFailover)
    {
        InMemorySnapshotStore store = snapshots ?? new InMemorySnapshotStore();

        PairReader pair = new(
            new LocalStateReader(
                provider ?? new FakeHypervProvider(FakeScenarios.Healthy(Now)),
                FakeHostSystemProvider.Target(),
                // The CN follows the machine being simulated: swapping the node and peer
                // blocks without swapping the certificate would trip the certificate rule and
                // refuse for a reason that has nothing to do with the failover.
                FakeCertificateProvider.Valid(
                    ValidDocument.LocalThumbprint, $"CN={machineName}"),
                new SilentDiagnosticLog()),
            peer ?? FakePeerChannel.Answering(
                FakeScenarios.PeerSnapshot(
                    Now, peerPublishesBuild ? peerBuild ?? Build : null)),
            store,
            new FixedClock(Now),
            Build,
            new SilentDiagnosticLog());

        return new FailoverQuery(
                new StubConfigStore(expectedRole, machineName),
                pair,
                provider ?? new FakeHypervProvider(FakeScenarios.Healthy(Now)),
                new InMemoryAuditLog(),
                new FixedClock(Now),
                FailoverTiming.ForTests)
            .ExecuteAsync(
                new FailoverCommand(
                    "ripcord.yaml", machineName, vmName, scenario, dryRun, "RH", Build),
                CancellationToken.None);
    }

    private sealed class StubConfigStore(string expectedRole, string machineName) : IConfigStore
    {
        public ConfigurationRead Read(string path)
        {
            ConfigurationDocument document = ValidDocument.Create();
            document.Replication!.ExpectedRole = expectedRole;

            // The validator refuses a configuration that names the other host, so the node and
            // peer blocks swap with the role being simulated.
            if (!string.Equals(
                machineName, FakeScenarios.LocalHostName, StringComparison.OrdinalIgnoreCase))
            {
                (document.Node!.Hostname, document.Peer!.Hostname) =
                    (document.Peer!.Hostname, document.Node!.Hostname);
            }

            return new ConfigurationRead(document, []);
        }
    }
}
