using Ripcord.Adapters.Fake;
using Ripcord.Cli;
using Ripcord.Domain;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Alerting;
using Ripcord.Ports.Audit;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Dashboard;
using Ripcord.Ports.Deployment;
using Ripcord.Ports.Pairing;
using Ripcord.Ports.Replication;
using Ripcord.Ports.Updates;
using Ripcord.Ports;
using Ripcord.Tests.Alerting;
using Ripcord.Tests.Updates;

namespace Ripcord.Tests.Cli;

/// The command that replaces the binary. Every test here is about what it refuses, because
/// the refusals are the feature: the one thing worse than a host that will not update is a
/// host that installs whatever it was handed.
public sealed class UpdateCliTests
{
    private const string Machine = FakeScenarios.LocalHostName;

    private static readonly DateTimeOffset Now = new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_dry_run_shows_the_plan_and_is_never_asked_to_confirm()
    {
        Swap swap = new();

        CliRun run = await Run(["update", "--dry-run"], swap: swap);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("Nothing was changed", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Type the node name", run.Output, StringComparison.Ordinal);
        Assert.Empty(swap.Moves);
    }

    [Fact]
    public async Task A_real_run_does_nothing_until_the_node_name_is_typed()
    {
        Swap swap = new();

        CliRun run = await Run(["update"], swap: swap, typed: "no");

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Empty(swap.Moves);
        Assert.Contains("nothing was changed", run.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_confirmed_update_installs_the_release()
    {
        Swap swap = new();

        CliRun run = await Run(["update"], swap: swap, typed: Machine);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Equal(
            [UpdateAction.DiscardPrevious, UpdateAction.SetAside, UpdateAction.Install],
            swap.Moves);
    }

    /// The binary that is running is still the old one. Saying so is the difference between a
    /// confusing `ripcord version` and a support call.
    [Fact]
    public async Task A_finished_update_says_the_new_version_starts_on_the_next_run()
    {
        CliRun run = await Run(["update"], typed: Machine);

        Assert.Contains("restart", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// The whole point of the feature. A release signed by anybody else is not installed, and
    /// the host is not touched on the way to finding that out.
    [Fact]
    public async Task A_release_signed_by_a_stranger_is_refused_after_the_name_was_typed()
    {
        Swap swap = new();

        CliRun run = await Run(
            ["update"], swap: swap, typed: Machine, source: Source.SignedByAStranger());

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Empty(swap.Moves);
        Assert.Contains("signature", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// A host that carries no signing key cannot tell a genuine release from any other, so it
    /// installs nothing. Refusing is the only safe reading of "I cannot check".
    [Fact]
    public async Task A_host_with_no_signing_key_installs_nothing()
    {
        Swap swap = new();

        CliRun run = await Run(["update"], swap: swap, typed: Machine, signingKey: null);

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Empty(swap.Moves);
    }

    [Fact]
    public async Task A_host_not_allowed_to_install_is_refused_by_name()
    {
        Swap swap = new();

        CliRun run = await Run(
            ["update"], swap: swap, typed: Machine, store: new Store(check: true, install: false));

        Assert.NotEqual(ExitCode.Success, run.Code);
        Assert.Empty(swap.Moves);
        Assert.Contains("updates.install", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_not_allowed_to_look_is_refused_before_anything_is_fetched()
    {
        Swap swap = new();

        CliRun run = await Run(
            ["update"], swap: swap, typed: Machine, store: new Store(check: false, install: false));

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Empty(swap.Moves);
        Assert.Contains("updates.check", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_already_on_the_latest_release_changes_nothing()
    {
        Swap swap = new();

        CliRun run = await Run(
            ["update"], swap: swap, typed: Machine, feed: StubReleaseFeed.Publishing("0.1.0"));

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Empty(swap.Moves);
    }

    /// Decision D54, said before the prompt rather than discovered during the next failover.
    [Fact]
    public async Task The_consequence_for_the_pair_is_printed_above_the_confirmation()
    {
        CliRun run = await Run(["update", "--dry-run"]);

        Assert.Contains("WARNING", run.Output, StringComparison.Ordinal);
        Assert.Contains("failover", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_option_is_refused_rather_than_ignored()
    {
        CliRun run = await Run(["update", "--yes"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("--yes", run.Error, StringComparison.Ordinal);
    }

    private static async Task<CliRun> Run(
        string[] args,
        Swap? swap = null,
        Source? source = null,
        Store? store = null,
        IReleaseFeed? feed = null,
        string? typed = null,
        string? signingKey = "carried",
        CancellationToken cancellationToken = default)
    {
        StringWriter output = new();
        StringWriter error = new();

        RipcordCli cli = new(
            new RipcordPorts(
                store ?? new Store(check: true, install: true),
                new FakeHypervProvider(FakeScenarios.Healthy(Now)),
                FakeHostSystemProvider.Target(),
                FakeCertificateProvider.Valid(
                    Tests.Configuration.ValidDocument.LocalThumbprint, "CN=HV-REPLICA-01"),
                FakePeerChannel.Absent(),
                new InMemorySnapshotStore(),
                new NoOpDeploymentExecutor(),
                new NoOpPeerListener(),
                new NoOpDashboardServer(),
                new InMemoryAuditLog(),
                new StubNotifier(),
                new MemoryAlertStateStore(),
                feed ?? StubReleaseFeed.Publishing("0.2.0"),
                source ?? Source.Genuine(),
                new MemoryUpdateNoticeStore(),
                swap ?? new Swap(),
                new FixedClock(Now),
                new SilentDiagnosticLog()),
            new CliEnvironment(
                Machine,
                "ripcord.yaml",
                @"D:\Ripcord\ripcord.exe",
                new StringReader(typed ?? ""),
                "tester",
                new BuildIdentity("0.1.0", "abc123def456"),
                signingKey is null ? null : Keys.Pinned));

        ExitCode code = await cli.RunAsync(args, output, error, cancellationToken);

        return new CliRun(code, output.ToString(), error.ToString());
    }

    private sealed record CliRun(ExitCode Code, string Output, string Error);

    /// Records the moves it was asked to make, and makes none of them.
    private sealed class Swap : IBinarySwap
    {
        public List<UpdateAction> Moves { get; } = [];

        public StagedBinaries Observe(string binaryPath) => new(false, false);

        public void Apply(UpdateStep move, StagedRelease release, CancellationToken cancellationToken) =>
            this.Moves.Add(move.Action);

        public void Restore(string binaryPath)
        {
        }
    }

    private sealed class Store(bool check, bool install) : IConfigStore
    {
        public ConfigurationRead Read(string path)
        {
            ConfigurationDocument document = Tests.Configuration.ValidDocument.Create();
            document.Node!.Hostname = Machine;
            document.Listener!.Enabled = false;
            document.Updates = new UpdatesDocument { Check = check, Install = install };

            return ConfigurationRead.Succeeded(document);
        }
    }
}
