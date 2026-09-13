using Ripcord.Adapters.Fake;
using Ripcord.Cli;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;
using Ripcord.Ports;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.TestFailover;

/// The command as an operator meets it. What is asserted here is mostly what the command
/// *refuses* to do: a mutating command that guesses is worse than one that stops.
public class TestFailoverCliTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Neither_vm_nor_all_is_refused_rather_than_guessed()
    {
        CliRun run = await Run(["test-failover"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("--vm", run.Error);
    }

    [Fact]
    public async Task All_and_vm_together_are_refused()
    {
        CliRun run = await Run(["test-failover", "--all", "--vm", "VM-DC-01"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("cannot be combined", run.Error);
    }

    [Fact]
    public async Task An_unknown_vm_name_is_refused_rather_than_silently_testing_nothing()
    {
        CliRun run = await Run(
            ["test-failover", "--vm", "VM-TYPO-01", "--dry-run"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("VM-TYPO-01", run.Error);
    }

    /// Rule 3: no mutating operation without an explicit typed confirmation. A test failover
    /// creates and destroys a real VM on the host.
    [Fact]
    public async Task A_real_run_does_nothing_until_the_node_name_is_typed()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Now));

        CliRun run = await Run(
            ["test-failover", "--vm", "VM-DC-01"], provider: host, typed: "not-the-node");

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Empty(host.Calls);
    }

    /// And `--dry-run` needs none, because it changes nothing by construction.
    [Fact]
    public async Task A_dry_run_needs_no_confirmation_and_changes_nothing()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Now));

        CliRun run = await Run(
            ["test-failover", "--vm", "VM-DC-01", "--dry-run"], provider: host);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("DRY RUN", run.Output);
        Assert.Contains("Nothing was changed", run.Output);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("create", StringComparison.Ordinal));
    }

    /// Run on the host that holds the primary copies there is nothing to test, and the
    /// operator is on the wrong machine — which is the mistake worth catching loudly.
    [Fact]
    public async Task On_the_primary_side_the_command_refuses_and_names_the_other_host()
    {
        CliRun run = await Run(
            ["test-failover", "--all", "--dry-run"],
            machineName: ValidDocument.PeerName,
            configStore: new StubConfigStore(document =>
            {
                document.Node!.Hostname = ValidDocument.PeerName;
                document.Peer!.Hostname = ValidDocument.MachineName;
                document.Replication!.ExpectedRole = "primary";
            }));

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Contains(ValidDocument.MachineName, run.Error);
    }

    /// Already running on the recovery side. A test copy taken now would be a copy of a live
    /// production VM, and the pair has a real incident to finish first.
    [Fact]
    public async Task A_failed_over_pair_refuses()
    {
        CliRun run = await Run(
            ["test-failover", "--all", "--dry-run"],
            provider: new FakeHypervProvider(FailedOver()));

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Contains("failed over", run.Error);
    }

    /// The local host could not be read at all, which is the tool failing rather than the
    /// infrastructure being wrong — a different code, and deliberately so.
    [Fact]
    public async Task A_host_that_cannot_be_read_is_a_local_access_failure()
    {
        CliRun run = await Run(
            ["test-failover", "--vm", "VM-DC-01", "--dry-run"],
            provider: FakeHypervProvider.FailingLocally("the WMI service is not running"));

        Assert.Equal(ExitCode.LocalAccessFailure, run.Code);
        Assert.Contains("WMI service", run.Error);
    }

    /// Interrupted during the pair read, which is still read-only. Exit 3 would tell a
    /// scheduler to investigate a local access failure that never happened.
    [Fact]
    public async Task An_interruption_during_the_read_reports_nothing_changed()
    {
        CliRun run = await Run(
            ["test-failover", "--vm", "VM-DC-01"], provider: new CancellingProvider());

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Contains("nothing was changed", run.Error);
    }

    [Fact]
    public async Task The_command_appears_in_the_usage()
    {
        Assert.Contains("test-failover", (await Run([])).Output);
    }

    private static async Task<CliRun> Run(
        string[] args,
        string machineName = FakeScenarios.LocalHostName,
        IHypervProvider? provider = null,
        IConfigStore? configStore = null,
        string? typed = FakeScenarios.LocalHostName)
    {
        StringWriter output = new();
        StringWriter error = new();

        RipcordCli cli = new(
            configStore ?? new StubConfigStore(),
            provider ?? new FakeHypervProvider(FakeScenarios.Healthy(Now)),
            FakeHostSystemProvider.Target(),
            FakeCertificateProvider.Valid(ValidDocument.LocalThumbprint, "CN=HV-REPLICA-01"),
            FakePeerChannel.Absent(),
            new InMemorySnapshotStore(),
            new NoOpDeploymentExecutor(),
            new NoOpPeerListener(),
            new FixedClock(Now),
            new CliEnvironment(
                machineName,
                @"C:\ProgramData\Ripcord\ripcord.yaml",
                @"C:\Program Files\Ripcord\ripcord.exe",
                new StringReader(typed ?? "")));

        ExitCode code = await cli.RunAsync(args, output, error, CancellationToken.None);

        return new CliRun(code, output.ToString(), error.ToString());
    }

    private sealed record CliRun(ExitCode Code, string Output, string Error);

    /// A P1 VM holding the primary copy on the host that normally holds the replicas: the
    /// observed state decision D20 derives `failed-over` from.
    private static HostState FailedOver() =>
        FakeScenarios.Healthy(Now) with
        {
            Vms =
            [
                .. FakeScenarios.Healthy(Now).Vms.Select(vm =>
                    vm.Name == "VM-DC-01" ? vm with { Role = ReplicationRole.Primary } : vm),
            ],
        };

    private sealed class CancellingProvider : ReadOnlyHypervProvider
    {
        public override Task<HostState> GetLocalStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromCanceled<HostState>(new CancellationToken(true));
    }

    private sealed class StubConfigStore(Action<ConfigurationDocument>? adjust = null)
        : IConfigStore
    {
        public ConfigurationRead Read(string path)
        {
            ConfigurationDocument document = ValidDocument.Create();
            adjust?.Invoke(document);
            return new ConfigurationRead(document, []);
        }
    }

}
