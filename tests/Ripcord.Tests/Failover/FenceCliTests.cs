using Ripcord.Adapters.Fake;
using Ripcord.Cli;
using Ripcord.Domain;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;
using Ripcord.Tests.Configuration;

using Ripcord.Tests.Alerting;
using Ripcord.Tests.Updates;

namespace Ripcord.Tests.Failover;

/// `ripcord fence` — the first command run on the original primary when it comes back from an
/// unplanned failover, before anything else is done to the pair. What it prevents is two
/// domain controllers with the same identity on the same subnet, which no later sequence
/// repairs.
public class FenceCliTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    private static readonly BuildIdentity Build = new("0.4.0", "abc123def456");

    [Fact]
    public async Task The_usage_names_the_fence_verb()
    {
        CliRun run = await Run([]);

        Assert.Contains("ripcord fence", run.Output, StringComparison.Ordinal);
    }

    /// Rule 3. It changes how this host behaves on its next boot, so it is confirmed.
    [Fact]
    public async Task A_real_run_does_nothing_until_the_node_name_is_typed()
    {
        FakeHypervProvider provider = Returning();

        CliRun run = await Run(["fence"], provider: provider, typed: "not-the-node");

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task A_dry_run_names_what_it_would_change_and_touches_nothing()
    {
        FakeHypervProvider provider = Returning();

        CliRun run = await Run(["fence", "--dry-run"], provider: provider, typed: null);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Empty(provider.Calls);
        Assert.Contains("VM-DC-01", run.Output, StringComparison.Ordinal);
    }

    /// The prior setting is printed because `reprotect` has to put it back, and because a host
    /// that comes home with every VM set to never start is a second outage at the next reboot.
    [Fact]
    public async Task The_dry_run_prints_the_setting_it_would_replace()
    {
        CliRun run = await Run(["fence", "--dry-run"], provider: Returning(), typed: null);

        Assert.Contains("StartIfRunning", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_run_sets_the_startup_action_to_nothing()
    {
        FakeHypervProvider provider = Returning();

        CliRun run = await Run(["fence"], provider: provider);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("start-action:VM-DC-01:Nothing", provider.Calls);
    }

    /// A copy already running here is not a future boot: it is the divergence, under way.
    /// Setting a start action would leave it running and report success.
    [Fact]
    public async Task A_vm_already_running_here_halts_the_fence()
    {
        FakeHypervProvider provider = Returning(VmPowerState.Running);

        CliRun run = await Run(["fence"], provider: provider);

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Empty(provider.Calls);
    }

    /// Nothing to do is not a failure. A host fenced twice reports that it is fenced.
    [Fact]
    public async Task A_host_already_fenced_succeeds_having_changed_nothing()
    {
        FakeHypervProvider provider = Returning(action: AutomaticStartAction.Nothing);

        CliRun run = await Run(["fence"], provider: provider);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task An_unexpected_argument_is_refused_rather_than_ignored()
    {
        CliRun run = await Run(["fence", "--force"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("--force", run.Error, StringComparison.Ordinal);
    }

    /// This host holds stale copies of the VMs that moved, still set to boot themselves.
    private static FakeHypervProvider Returning(
        VmPowerState power = VmPowerState.Off,
        AutomaticStartAction action = AutomaticStartAction.StartIfRunning) =>
        new(new HostState(
            FakeScenarios.LocalHostName,
            [
                new VmReplicationState(
                    "VM-DC-01",
                    ReplicationRole.Primary,
                    ReplicationState.Replicating,
                    ReplicationHealth.Critical,
                    null,
                    null,
                    null,
                    power,
                    action),
            ],
            HostReachability.Reachable(),
            HostFacts.Unknown()));

    private static async Task<CliRun> Run(
        string[] args,
        IHypervProvider? provider = null,
        string? typed = FakeScenarios.LocalHostName)
    {
        StringWriter output = new();
        StringWriter error = new();

        RipcordCli cli = new(
            new StubConfigStore(),
            provider ?? new FakeHypervProvider(FakeScenarios.Healthy(Now)),
            FakeHostSystemProvider.Target(),
            FakeCertificateProvider.Valid(ValidDocument.LocalThumbprint, "CN=HV-REPLICA-01"),
            FakePeerChannel.Absent(),
            new InMemorySnapshotStore(),
            new NoOpDeploymentExecutor(),
            new NoOpPeerListener(),
            new InMemoryAuditLog(),
            new StubNotifier(),
            new MemoryAlertStateStore(),
            StubReleaseFeed.Unreachable(),
            new FixedClock(Now),
            new CliEnvironment(
                FakeScenarios.LocalHostName,
                @"C:\ProgramData\Ripcord\ripcord.yaml",
                @"C:\Program Files\Ripcord\ripcord.exe",
                new StringReader(typed ?? ""),
                "RH",
                Build));

        ExitCode code = await cli.RunAsync(args, output, error, CancellationToken.None);

        return new CliRun(code, output.ToString(), error.ToString());
    }

    private sealed class StubConfigStore : IConfigStore
    {
        public ConfigurationRead Read(string path) => new(ValidDocument.Create(), []);
    }

    private sealed record CliRun(ExitCode Code, string Output, string Error);
}
