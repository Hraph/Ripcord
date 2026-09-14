using Ripcord.Ports.Dashboard;
using Ripcord.Adapters.Fake;
using Ripcord.Cli;
using Ripcord.Domain;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;
using Ripcord.Domain.Configuration;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;
using Ripcord.Tests.Configuration;

using Ripcord.Tests.Alerting;
using Ripcord.Tests.Updates;

namespace Ripcord.Tests.Failover;

/// The command as an operator meets it, at a KVM, at three in the morning. Most of what is
/// asserted here is what it refuses to do: this is the command that shuts production down, and
/// one that guesses is far worse than one that stops.
public class FailoverCliTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    /// Both hosts on the same build. A pair running two versions refuses every mutating
    /// command, so a test meant to reach one has to put the same binary on both sides.
    private static readonly BuildIdentity Build = new("0.4.0", "abc123def456");

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
            ["failover", "--scenario", "sideways", "--vm", "VM-DC-01", "--dry-run"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("sideways", run.Error, StringComparison.Ordinal);
    }

    /// The unplanned plan runs entirely here, and the dry run says so — three steps, all on
    /// this host, none of them waiting on the machine that is gone.
    [Fact]
    public async Task An_unplanned_dry_run_prints_a_plan_confined_to_this_host()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "unplanned", "--vm", "VM-DC-01", "--dry-run"],
            typed: null);

        Assert.Equal(ExitCode.Success, run.Code);

        // Every step's host column is this one. The peer is named further down, as the host to
        // go and fence — not as a host with a step in this plan.
        Assert.DoesNotContain(
            $"{FakeScenarios.PeerHostName}  ", run.Output, StringComparison.Ordinal);
    }

    /// The command printed for the operator to type must be the one they typed. An unplanned
    /// dry run that hands back `--scenario planned` sends them to run the wrong sequence, off
    /// a screen they are reading precisely because they cannot compose it from memory.
    [Fact]
    public async Task An_unplanned_dry_run_hands_back_the_unplanned_command()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "unplanned", "--vm", "VM-DC-01", "--dry-run"],
            typed: null);

        Assert.Contains("--scenario unplanned", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("--scenario planned", run.Output, StringComparison.Ordinal);
    }

    /// The step that is easiest to leave out, and the one that costs the most when it is. Once
    /// production is running here, the original primary still holds a copy of every VM that
    /// moved, set to start itself — so the run has to end by telling the operator what to do
    /// the moment that host comes back, while they are still reading the screen.
    [Fact]
    public async Task An_unplanned_run_says_to_fence_the_other_host_when_it_returns()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "unplanned", "--vm", "VM-DC-01", "--dry-run"],
            typed: null);

        Assert.Contains("ripcord fence", run.Output, StringComparison.Ordinal);
        Assert.Contains(FakeScenarios.PeerHostName, run.Output, StringComparison.Ordinal);
    }

    /// The confirmation prompt is where the operator learns what they are about to lose.
    /// A planned failover sends the last changes across first; an unplanned one cannot, so
    /// everything written since the last replication cycle is gone. Printing the planned
    /// wording here would be the tool concealing the cost of the command being typed.
    [Fact]
    public async Task The_unplanned_confirmation_names_the_data_that_will_be_lost()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "unplanned", "--vm", "VM-DC-01"],
            typed: "not-the-node");

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Contains("lost", run.Output, StringComparison.OrdinalIgnoreCase);
    }

    /// One form at a time. `--all --vm VM-DC-01` has no reading that is obviously right, and
    /// guessing at one moves production.
    [Fact]
    public async Task Naming_a_vm_and_sweeping_at_once_is_refused()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "unplanned", "--all", "--vm", "VM-DC-01", "--dry-run"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
    }

    /// A sweep fails the machines over in priority order, and says which it left out. D19's
    /// backup VM is `failover: manual` and must not be in the list.
    [Fact]
    public async Task A_sweep_takes_the_auto_vms_in_priority_order_and_names_the_excluded_one()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "unplanned", "--all", "--dry-run"], typed: null);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("VM-DC-01", run.Output, StringComparison.Ordinal);

        // The exclusion is a note, like the degradations `status` prints: it belongs next to
        // the run rather than inside the plan, and it must be said out loud either way.
        Assert.Contains("VM-BACKUP-01", run.Error, StringComparison.Ordinal);
        Assert.Contains("manual", run.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_priority_sweep_is_refused_when_the_tier_is_not_a_declared_one()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "unplanned", "--priority", "P9", "--dry-run"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("P9", run.Error, StringComparison.Ordinal);
    }

    /// Neither a VM nor a sweep is not a failover of everything. It is a missing argument.
    [Fact]
    public async Task A_failover_with_no_vm_and_no_sweep_is_refused()
    {
        CliRun run = await Run(["failover", "--scenario", "unplanned", "--dry-run"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
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
            peerChannel: FakePeerChannel.Answering(
                FakeScenarios.PeerSnapshot(Now, Build)));

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

    /// The line handed to the operator has to be one they can type. `failback` refuses
    /// `--scenario`, and `failover --scenario planned` builds a plan with the two hosts the
    /// other way round — so printing that here sends somebody to run the wrong sequence, off
    /// the screen they are reading precisely because they cannot compose it from memory.
    [Fact]
    public async Task A_failback_dry_run_hands_back_the_failback_command()
    {
        CliRun run = await Run(
            ["failback", "--vm", "VM-DC-01", "--dry-run"],
            provider: new FakeHypervProvider(FakeScenarios.Healthy(Now)),
            typed: null,
            peerChannel: FakePeerChannel.Answering(
                FakeScenarios.PeerSnapshot(Now, Build)));

        Assert.Contains("ripcord failback --vm", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("--scenario", run.Output, StringComparison.Ordinal);
    }

    /// `failback` is one operation. A `--scenario` on it would be the operator naming a second
    /// one, and the two could only disagree.
    [Fact]
    public async Task Failback_takes_no_scenario()
    {
        CliRun run = await Run(
            ["failback", "--scenario", "planned", "--vm", "VM-DC-01", "--dry-run"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("--scenario", run.Error, StringComparison.Ordinal);
    }

    /// The scope rules are the same ones `failover` uses, because they are the same code —
    /// including the `manual` VM that no sweep picks up.
    [Fact]
    public async Task Failback_shares_the_scope_rules_with_failover()
    {
        CliRun run = await Run(["failback", "--dry-run"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("--vm", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_usage_names_the_failback_verb()
    {
        CliRun run = await Run([]);

        Assert.Contains("ripcord failback", run.Output, StringComparison.Ordinal);
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
            new RipcordPorts(
                new StubConfigStore(),
                provider ?? new FakeHypervProvider(FakeScenarios.Healthy(Now)),
                FakeHostSystemProvider.Target(),
                FakeCertificateProvider.Valid(ValidDocument.LocalThumbprint, "CN=HV-REPLICA-01"),
                peerChannel ?? FakePeerChannel.Absent(),
                new InMemorySnapshotStore(),
                new NoOpDeploymentExecutor(),
                new NoOpPeerListener(),
                new NoOpDashboardServer(),
                new InMemoryAuditLog(),
                new StubNotifier(),
                new MemoryAlertStateStore(),
                StubReleaseFeed.Unreachable(),
                new NoReleaseSource(),
                new MemoryUpdateNoticeStore(),
                new NoBinarySwap(),
                new FixedClock(Now)),
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

    /// The operator is at a KVM on one host and has to continue on the other. Naming the host
    /// is not enough — they need the line to type, so it can be read off one screen and entered
    /// on another without composing it from memory.
    [Fact]
    public async Task The_output_gives_the_command_to_run_on_the_other_host()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "planned", "--vm", "VM-DC-01", "--dry-run"],
            provider: new FakeHypervProvider(FakeScenarios.Healthy(Now)),
            typed: null,
            peerChannel: FakePeerChannel.Answering(FakeScenarios.PeerSnapshot(Now, Build)));

        Assert.Contains(
            "ripcord failover --scenario planned --vm VM-DC-01",
            run.Output,
            StringComparison.Ordinal);
    }

    /// A pair on two versions would execute half a sequence written by each. It refuses, and it
    /// refuses the dry run too: a plan produced by a binary that will not be running the other
    /// half is not the plan that would run.
    [Fact]
    public async Task A_version_mismatch_refuses_rather_than_warning()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "planned", "--vm", "VM-DC-01", "--dry-run"],
            typed: null,
            peerChannel: FakePeerChannel.Answering(
                FakeScenarios.PeerSnapshot(Now, new BuildIdentity("0.3.0", "999999999999"))));

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Contains("0.3.0", run.Error, StringComparison.Ordinal);
    }

    /// A peer that never said which binary it runs cannot be shown to match. "Could not be
    /// established" must not read as "the same".
    [Fact]
    public async Task A_peer_that_published_no_build_is_refused_rather_than_assumed_equal()
    {
        CliRun run = await Run(
            ["failover", "--scenario", "planned", "--vm", "VM-DC-01", "--dry-run"],
            typed: null,
            peerChannel: FakePeerChannel.Answering(FakeScenarios.PeerSnapshot(Now)));

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Contains(
            "not running the same Ripcord", run.Error, StringComparison.Ordinal);
    }

    private sealed record CliRun(ExitCode Code, string Output, string Error);

    private sealed class StubConfigStore : IConfigStore
    {
        public ConfigurationRead Read(string path) =>
            new(ValidDocument.Create(), []);
    }
}
