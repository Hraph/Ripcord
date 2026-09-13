using Ripcord.Adapters.Fake;
using Ripcord.Cli;
using Ripcord.Domain;
using Ripcord.Domain.Replication;
using Ripcord.Domain.Configuration;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Failover;

/// The command as an operator meets it, at a KVM, at three in the morning. Most of what is
/// asserted here is what it refuses to do: this is the command that shuts production down, and
/// one that guesses is far worse than one that stops.
public class FailoverCliTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    /// Planned and unplanned are different operations with different consequences. Defaulting
    /// would let the wrong one run because nobody typed the word.
    [Fact]
    public async Task The_scenario_is_required_rather_than_defaulted()
    {
        CliRun run = await Run(["failover", "--vm", "VM-DC-01", "--dry-run"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("--scenario", run.Error, StringComparison.Ordinal);
    }

    /// A scenario this binary does not implement is refused by name. Silently treating it as
    /// planned would fail production over the wrong way.
    [Fact]
    public async Task An_unimplemented_scenario_is_refused_by_name()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "unplanned", "--vm", "VM-DC-01", "--dry-run"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("unplanned", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_vm_is_refused()
    {
        CliRun run = await Run(["failover", "--scenario", "planned", "--dry-run"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("--vm", run.Error, StringComparison.Ordinal);
    }

    /// Rule 3, at its sharpest. Nothing happens until the node name is typed in full.
    [Fact]
    public async Task A_real_run_does_nothing_until_the_node_name_is_typed()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Now));

        CliRun run = await Run(
            ["failover", "--scenario", "planned", "--vm", "VM-DC-01"],
            provider: host,
            typed: "not-the-node");

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Empty(host.Calls);
    }

    /// `--dry-run` is exempt from the confirmation, because it changes nothing — and it must
    /// change nothing even when the operator types nothing at all.
    [Fact]
    public async Task A_dry_run_needs_no_confirmation_and_touches_nothing()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Now));

        CliRun run = await Run(
            ["failover", "--scenario", "planned", "--vm", "VM-DC-01", "--dry-run"],
            provider: host,
            typed: null);

        Assert.Empty(host.Calls);
        Assert.NotEqual(ExitCode.LocalAccessFailure, run.Code);
    }

    /// The plan is the deliverable of a dry run, and every step has to name its host — the
    /// operator has to see which half means walking to the other machine.
    [Fact]
    public async Task The_dry_run_prints_every_step_against_a_host()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "planned", "--vm", "VM-DC-01", "--dry-run"],
            provider: new FakeHypervProvider(FakeScenarios.Healthy(Now)),
            typed: null,
            peerChannel: FakePeerChannel.Answering(FakeScenarios.PeerSnapshot(Now)));

        Assert.Contains("HOST", run.Output, StringComparison.Ordinal);
        Assert.Contains(FakeScenarios.PeerHostName, run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_vm_is_refused_rather_than_failing_over_nothing()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "planned", "--vm", "VM-TYPO-01", "--dry-run"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("VM-TYPO-01", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unexpected_argument_is_refused_rather_than_ignored()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "planned", "--vm", "VM-DC-01", "--force"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("--force", run.Error, StringComparison.Ordinal);
    }

    /// The verb appears in the usage, because a command nobody can discover is a command
    /// nobody runs on the day.
    [Fact]
    public async Task The_usage_names_the_failover_verb()
    {
        CliRun run = await Run([]);

        Assert.Contains("ripcord failover", run.Output, StringComparison.Ordinal);
    }

    private static async Task<CliRun> Run(
        string[] args,
        IHypervProvider? provider = null,
        string? typed = FakeScenarios.LocalHostName,
        FakePeerChannel? peerChannel = null)
    {
        StringWriter output = new();
        StringWriter error = new();

        RipcordCli cli = new(
            new StubConfigStore(),
            provider ?? new FakeHypervProvider(FakeScenarios.Healthy(Now)),
            FakeHostSystemProvider.Target(),
            FakeCertificateProvider.Valid(ValidDocument.LocalThumbprint, "CN=HV-REPLICA-01"),
            peerChannel ?? FakePeerChannel.Absent(),
            new InMemorySnapshotStore(),
            new NoOpDeploymentExecutor(),
            new NoOpPeerListener(),
            new InMemoryAuditLog(),
            new FixedClock(Now),
            new CliEnvironment(
                FakeScenarios.LocalHostName,
                @"C:\ProgramData\Ripcord\ripcord.yaml",
                @"C:\Program Files\Ripcord\ripcord.exe",
                new StringReader(typed ?? ""),
                "RH"));

        ExitCode code = await cli.RunAsync(args, output, error, CancellationToken.None);

        return new CliRun(code, output.ToString(), error.ToString());
    }

    private sealed record CliRun(ExitCode Code, string Output, string Error);

    private sealed class StubConfigStore : IConfigStore
    {
        public ConfigurationRead Read(string path) =>
            new(ValidDocument.Create(), []);
    }
}
