using Ripcord.Adapters.Fake;
using Ripcord.Cli;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports;

namespace Ripcord.Tests;

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

    private const string DefaultConfigPath = "/opt/ripcord/ripcord.yaml";

    private static async Task<CliRun> Run(
        string[] args,
        string machineName = FakeScenarios.LocalHostName,
        IHypervProvider? provider = null,
        IConfigStore? configStore = null,
        CancellationToken cancellationToken = default)
    {
        StringWriter output = new();
        StringWriter error = new();

        RipcordCli cli = new(
            configStore ?? new RecordingConfigStore(),
            provider ?? new FakeHypervProvider(
                FakeScenarios.Healthy(Now), FakeScenarios.AbsentPeer()),
            new FixedClock(Now),
            new CliEnvironment(machineName, DefaultConfigPath));

        ExitCode code = await cli.RunAsync(args, output, error, cancellationToken);

        return new CliRun(code, output.ToString(), error.ToString());
    }

    private sealed record CliRun(ExitCode Code, string Output, string Error);

    private sealed class CancellingProvider : IHypervProvider
    {
        public Task<HostState> GetLocalStateAsync(CancellationToken cancellationToken) =>
            Task.FromCanceled<HostState>(
                cancellationToken.IsCancellationRequested ? cancellationToken : new(true));

        public Task<HostState> GetPeerStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(FakeScenarios.AbsentPeer());
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// Returns a document the validator accepts, and remembers which path was asked for.
    private sealed class RecordingConfigStore : IConfigStore
    {
        public string? RequestedPath { get; private set; }

        public ConfigurationRead Read(string path)
        {
            this.RequestedPath = path;
            return ConfigurationRead.Succeeded(new Domain.Configuration.ConfigurationDocument
            {
                SchemaVersion = 1,
                Node = new Domain.Configuration.NodeDocument
                {
                    Hostname = FakeScenarios.LocalHostName,
                },
                Peer = new Domain.Configuration.PeerDocument
                {
                    Hostname = FakeScenarios.PeerHostName,
                    Address = "192.0.2.11",
                    OfflineAfterSec = 120,
                },
                Vms =
                [
                    new Domain.Configuration.VmDocument { Name = "VM-DC-01", Priority = "P1" },
                ],
            });
        }
    }
}
