using Ripcord.Adapters.Fake;
using Ripcord.Cli;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;
using Ripcord.Domain.Deployment;
using Ripcord.Ports;
using Ripcord.Domain.Pairing;
using Ripcord.Ports.Deployment;
using Ripcord.Ports.Pairing;

namespace Ripcord.Tests.Cli;

/// The command surface as the operator meets it: what gets printed, where, and what the
/// process exits with.
public class RipcordCliTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Version_prints_one_line_carrying_the_commit_hash()
    {
        CliRun run = await Run(["version"]);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Equal($"ripcord {BuildInfo.VersionWithCommit}", run.Output.Trim());
        Assert.Empty(run.Error);
    }

    [Fact]
    public async Task Status_renders_both_sections_and_exits_zero()
    {
        CliRun run = await Run(["status"]);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("LOCAL", run.Output, StringComparison.Ordinal);
        Assert.Contains("PEER", run.Output, StringComparison.Ordinal);
        Assert.Empty(run.Error);
    }

    /// Configuration errors go to stderr as a list, so a scheduled task's mail contains the
    /// whole list rather than the first line of it.
    [Fact]
    public async Task An_invalid_configuration_lists_every_error_on_stderr_and_exits_two()
    {
        CliRun run = await Run(["status"], machineName: "SOME-OTHER-HOST");

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("node.hostname", run.Error, StringComparison.Ordinal);
        Assert.Empty(run.Output);
    }

    [Fact]
    public async Task A_local_access_failure_exits_three_with_the_reason_on_stderr()
    {
        CliRun run = await Run(["status"], provider: FakeHypervProvider.FailingLocally("WMI is down"));

        Assert.Equal(ExitCode.LocalAccessFailure, run.Code);
        Assert.Contains("WMI is down", run.Error, StringComparison.Ordinal);
    }

    /// Under pressure the likeliest input is the bare command; usage has to be the answer,
    /// not an error.
    [Fact]
    public async Task No_command_prints_usage_and_exits_zero()
    {
        CliRun run = await Run([]);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("ripcord status", run.Output, StringComparison.Ordinal);
        Assert.Contains("ripcord version", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_command_names_it_and_exits_non_zero()
    {
        CliRun run = await Run(["failover"]);

        Assert.NotEqual(ExitCode.Success, run.Code);
        Assert.Contains("failover", run.Error, StringComparison.Ordinal);
    }

    /// Ctrl+C during a status read is a documented path: Program wires CancelKeyPress to the
    /// token. It must end in a line and an exit code, never a stack trace — rule 5.
    [Fact]
    public async Task Cancellation_ends_in_a_message_rather_than_a_stack_trace()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        CliRun run = await Run(
            ["status"], provider: new CancellingProvider(), cancellationToken: cancellation.Token);

        Assert.Equal(ExitCode.LocalAccessFailure, run.Code);
        Assert.Contains("cancelled", run.Error, StringComparison.OrdinalIgnoreCase);
    }

    /// Two --config options means one of the two files is silently ignored, and reading the
    /// wrong host's configuration is the failure this tool exists to prevent.
    [Fact]
    public async Task A_repeated_config_option_is_refused_rather_than_resolved_silently()
    {
        CliRun run = await Run(["status", "--config", "a.yaml", "--config", "b.yaml"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("--config", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_configuration_path_can_be_overridden()
    {
        RecordingConfigStore store = new();
        await Run(["status", "--config", "/etc/ripcord/other.yaml"], configStore: store);

        Assert.Equal("/etc/ripcord/other.yaml", store.RequestedPath);
    }

    [Fact]
    public async Task The_configuration_path_defaults_to_the_one_beside_the_binary()
    {
        RecordingConfigStore store = new();
        await Run(["status"], configStore: store);

        Assert.Equal(DefaultConfigPath, store.RequestedPath);
    }

    [Fact]
    public async Task A_config_option_with_no_value_is_refused_rather_than_ignored()
    {
        CliRun run = await Run(["status", "--config"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("--config", run.Error, StringComparison.Ordinal);
    }

    /// `--dry-run` is the output that gets read on the day, so it has to say exactly what
    /// would change, and change nothing.
    [Fact]
    public async Task Deploy_listener_dry_run_lists_the_steps_and_changes_nothing()
    {
        FakeDeploymentExecutor executor = new();

        CliRun run = await Run(["deploy-listener", "--dry-run"], deploymentExecutor: executor);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("service", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("7443", run.Output, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", run.Output, StringComparison.Ordinal);
        Assert.Empty(executor.Applied);
    }

    /// Rule 3: no mutating operation without explicit typed confirmation. A wrong answer
    /// leaves the host untouched.
    [Fact]
    public async Task Deploy_listener_changes_nothing_until_the_node_name_is_typed()
    {
        FakeDeploymentExecutor executor = new();

        CliRun run = await Run(
            ["deploy-listener"], deploymentExecutor: executor, typed: "yes");

        Assert.NotEqual(ExitCode.Success, run.Code);
        Assert.Empty(executor.Applied);
        Assert.Contains("nothing was changed", run.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Deploy_listener_applies_every_step_once_confirmed()
    {
        FakeDeploymentExecutor executor = new();

        CliRun run = await Run(
            ["deploy-listener"],
            deploymentExecutor: executor,
            typed: FakeScenarios.LocalHostName);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Equal(
            [DeploymentAction.CreateService, DeploymentAction.CreateFirewallRule,
             DeploymentAction.GrantSnapshotAccess],
            executor.Applied);
    }

    /// The confirmation is case-insensitive: host names are, and forcing the operator to match
    /// case at 3 a.m. buys nothing.
    [Fact]
    public async Task The_confirmation_accepts_the_node_name_in_any_case()
    {
        FakeDeploymentExecutor executor = new();

        await Run(
            ["deploy-listener"],
            deploymentExecutor: executor,
            typed: FakeScenarios.LocalHostName.ToLowerInvariant());

        Assert.NotEmpty(executor.Applied);
    }

    [Fact]
    public async Task Deploy_listener_remove_undoes_the_deployment_in_reverse()
    {
        FakeDeploymentExecutor executor = new(Deployed());

        CliRun run = await Run(
            ["deploy-listener", "--remove"],
            deploymentExecutor: executor,
            typed: FakeScenarios.LocalHostName);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Equal(
            [DeploymentAction.RevokeSnapshotAccess, DeploymentAction.RemoveFirewallRule,
             DeploymentAction.RemoveService],
            executor.Applied);
    }

    /// Re-running a correct deployment must be safe and obviously uneventful.
    [Fact]
    public async Task Deploy_listener_on_an_already_correct_host_does_nothing()
    {
        FakeDeploymentExecutor executor = new(Deployed());

        CliRun run = await Run(["deploy-listener"], deploymentExecutor: executor);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("already matches", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(executor.Applied);
    }

    /// A misspelt `--dry-run` that silently became a real run is the worst possible outcome
    /// on a mutating command.
    [Fact]
    public async Task An_unknown_option_on_a_mutating_command_is_refused()
    {
        FakeDeploymentExecutor executor = new();

        CliRun run = await Run(["deploy-listener", "--dryrun"], deploymentExecutor: executor);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Empty(executor.Applied);
    }

    /// The documented off switch: a node with the listener disabled must degrade to the
    /// milestone 1 local-only view, not fail.
    [Fact]
    public async Task Serve_on_a_node_with_the_listener_disabled_exits_zero()
    {
        CliRun run = await Run(
            ["serve"], configStore: new RecordingConfigStore(listenerEnabled: false));

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("disabled", run.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Serve_refuses_an_unknown_option_rather_than_ignoring_it()
    {
        CliRun run = await Run(["serve", "--wat"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
    }

    private static ObservedDeployment Deployed() => new(
        ServiceInstalled: true,
        ServiceBinaryPath: BinaryPath,
        FirewallRuleInstalled: true,
        FirewallPort: 7443,
        FirewallRemoteAddress: "192.0.2.11",
        SnapshotReadableByService: true);

    private const string DefaultConfigPath = "/opt/ripcord/ripcord.yaml";

    private const string BinaryPath = "/opt/ripcord/ripcord";

    private static async Task<CliRun> Run(
        string[] args,
        string machineName = FakeScenarios.LocalHostName,
        IHypervProvider? provider = null,
        IConfigStore? configStore = null,
        IDeploymentExecutor? deploymentExecutor = null,
        string? typed = null,
        CancellationToken cancellationToken = default)
    {
        StringWriter output = new();
        StringWriter error = new();

        RipcordCli cli = new(
            configStore ?? new RecordingConfigStore(),
            provider ?? new FakeHypervProvider(FakeScenarios.Healthy(Now)),
            FakeHostSystemProvider.Target(),
            FakeCertificateProvider.Valid(
                Tests.Configuration.ValidDocument.LocalThumbprint, "CN=HV-REPLICA-01"),
            FakePeerChannel.Absent(),
            new InMemorySnapshotStore(),
            deploymentExecutor ?? new FakeDeploymentExecutor(),
            new NoOpPeerListener(),
            new FixedClock(Now),
            new CliEnvironment(
                machineName, DefaultConfigPath, BinaryPath, new StringReader(typed ?? "")));

        ExitCode code = await cli.RunAsync(args, output, error, cancellationToken);

        return new CliRun(code, output.ToString(), error.ToString());
    }

    private sealed record CliRun(ExitCode Code, string Output, string Error);

    private sealed class CancellingProvider : IHypervProvider
    {
        public Task<HostState> GetLocalStateAsync(CancellationToken cancellationToken) =>
            Task.FromCanceled<HostState>(
                cancellationToken.IsCancellationRequested ? cancellationToken : new(true));

    }

    /// Reports a bare host, and records what it was asked to change.
    private sealed class FakeDeploymentExecutor(ObservedDeployment? observed = null)
        : IDeploymentExecutor
    {
        private readonly List<DeploymentAction> applied = [];

        public IReadOnlyList<DeploymentAction> Applied => this.applied;

        public ObservedDeployment Observe(DesiredDeployment desired) =>
            observed ?? ObservedDeployment.Nothing;

        public void Apply(DeploymentStep change, DesiredDeployment desired) =>
            this.applied.Add(change.Action);
    }

    private sealed class NoOpPeerListener : IPeerListener
    {
        public Task RunAsync(
            ListenerSettings settings, PeerRules rules, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// Returns a document the validator accepts, and remembers which path was asked for.
    private sealed class RecordingConfigStore(bool listenerEnabled = true) : IConfigStore
    {
        public string? RequestedPath { get; private set; }

        public ConfigurationRead Read(string path)
        {
            this.RequestedPath = path;
            ConfigurationDocument document = Tests.Configuration.ValidDocument.Create();
            document.Listener!.Enabled = listenerEnabled;
            document.Listener.SnapshotPath = "state.json";

            return ConfigurationRead.Succeeded(document);
        }
    }
}
