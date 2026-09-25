using Ripcord.Adapters.Fake;
using Ripcord.Domain.Alerting;
using Ripcord.Tests.Alerting;
using Ripcord.Tests.Updates;
using Ripcord.Cli;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;
using Ripcord.Domain.Deployment;
using Ripcord.Ports.Diagnostics;
using Ripcord.Domain.Diagnostics;
using Ripcord.Ports;
using Ripcord.Domain.Pairing;
using Ripcord.Ports.Dashboard;
using Ripcord.Ports.Deployment;
using Ripcord.Ports.Alerting;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Updates;
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

    [Fact]
    public async Task Check_renders_the_report_and_exits_zero_on_a_clean_pair()
    {
        CliRun run = await Run(["check"]);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("RIPCORD CHECK", run.Output, StringComparison.Ordinal);
        Assert.Contains("FEASIBILITY", run.Output, StringComparison.Ordinal);
        Assert.Empty(run.Error);
    }

    /// The one command whose exit code reports on the infrastructure rather than on the tool.
    [Fact]
    public async Task Check_exits_one_when_a_critical_rule_is_violated()
    {
        CliRun run = await Run(
            ["check"],
            provider: new FakeHypervProvider(FakeScenarios.UnreadyForFailover(Now)));

        Assert.Equal(ExitCode.CriticalFinding, run.Code);
        Assert.Contains("NOT READY", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_refuses_an_unexpected_argument_rather_than_guessing()
    {
        CliRun run = await Run(["check", "--all"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("unexpected argument", run.Error, StringComparison.Ordinal);
    }

    /// `serve` takes --config and nothing else. It once borrowed the deployment parser, which
    /// accepted --dry-run and --remove and then ignored both — an option that appears to be
    /// read and is not is worse here than one that is refused.
    [Theory]
    [InlineData("--dry-run")]
    [InlineData("--remove")]
    public async Task Serve_refuses_an_option_it_does_not_have(string option)
    {
        CliRun run = await Run(["serve", option]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("unexpected argument", run.Error, StringComparison.Ordinal);
    }

    /// The scheduled task's form of the command: the finding is on the console and on its way
    /// to a human at the same time.
    [Fact]
    public async Task Check_with_notify_sends_when_a_critical_rule_is_violated()
    {
        StubNotifier notifier = new();

        CliRun run = await Run(
            ["check", "--notify"],
            provider: new FakeHypervProvider(FakeScenarios.UnreadyForFailover(Now)),
            configStore: new RecordingConfigStore(alerting: true),
            notifier: notifier);

        Assert.Equal(ExitCode.CriticalFinding, run.Code);
        Assert.Equal(AlertKind.Raised, Assert.Single(notifier.Sent).Kind);
        Assert.Contains("notified ops@example.net", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_with_notify_sends_nothing_on_a_clean_pair()
    {
        StubNotifier notifier = new();

        CliRun run = await Run(
            ["check", "--notify"],
            configStore: new RecordingConfigStore(alerting: true),
            notifier: notifier);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Empty(notifier.Sent);
        Assert.Empty(run.Error);
    }

    /// Read before the scheduled task is switched on, which is the whole point of it.
    [Fact]
    public async Task Check_with_notify_and_dry_run_says_where_it_would_go_and_sends_nothing()
    {
        StubNotifier notifier = new();

        CliRun run = await Run(
            ["check", "--notify", "--dry-run"],
            provider: new FakeHypervProvider(FakeScenarios.UnreadyForFailover(Now)),
            configStore: new RecordingConfigStore(alerting: true),
            notifier: notifier);

        Assert.Empty(notifier.Sent);
        Assert.Contains("would notify", run.Error, StringComparison.Ordinal);
        Assert.Equal(ExitCode.CriticalFinding, run.Code);
    }

    /// A relay that is down is not a pair that is broken: the exit code still answers the
    /// question the command was asked, and the failure is said out loud.
    [Fact]
    public async Task Check_with_notify_that_reaches_nobody_still_reports_the_pair()
    {
        StubNotifier notifier = new()
        {
            Outcome = new DeliveryOutcome([], ["smtp.example.net: connection refused"]),
        };

        CliRun run = await Run(
            ["check", "--notify"],
            provider: new FakeHypervProvider(FakeScenarios.UnreadyForFailover(Now)),
            configStore: new RecordingConfigStore(alerting: true),
            notifier: notifier);

        Assert.Equal(ExitCode.CriticalFinding, run.Code);
        Assert.Contains("could not notify", run.Error, StringComparison.Ordinal);
    }

    /// Asked to notify by a host that notifies nobody. The check still stands, and the
    /// mismatch is not allowed to be quiet.
    [Fact]
    public async Task Check_with_notify_on_a_host_with_no_alerting_block_says_so()
    {
        CliRun run = await Run(["check", "--notify"]);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("alerting is switched off", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_refuses_dry_run_without_notify()
    {
        CliRun run = await Run(["check", "--dry-run"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("--dry-run only means anything", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_reads_the_configuration_path_it_was_given()
    {
        RecordingConfigStore store = new();

        await Run(["check", "--config", "elsewhere/ripcord.yaml"], configStore: store);

        // Resolved in full before anything reads it — the snapshot defaults beside this file
        // and the folder access is granted on comes from it, so neither may depend on the
        // directory the command happened to be run from.
        //
        // Asserted as a rule rather than as a literal: what "in full" means is the host's
        // answer, and this suite runs on Linux and, at every release, on Windows.
        Assert.NotEqual("elsewhere/ripcord.yaml", store.RequestedPath);
        Assert.EndsWith("ripcord.yaml", store.RequestedPath, StringComparison.Ordinal);
        Assert.NotEqual(DefaultConfigPath, store.RequestedPath);
    }

    [Fact]
    public async Task Check_on_a_local_access_failure_exits_three_with_the_reason()
    {
        CliRun run = await Run(
            ["check"],
            provider: FakeHypervProvider.FailingLocally("the WMI service is not running"));

        Assert.Equal(ExitCode.LocalAccessFailure, run.Code);
        Assert.Contains("the WMI service is not running", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_usage_names_check_and_its_exit_code()
    {
        CliRun run = await Run([]);

        Assert.Contains("ripcord check", run.Output, StringComparison.Ordinal);
        Assert.Contains("1 a critical rule is violated", run.Output, StringComparison.Ordinal);
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
    public async Task Service_dry_run_lists_the_steps_and_changes_nothing()
    {
        FakeDeploymentExecutor executor = new();

        CliRun run = await Run(["service", "install", "--dry-run"], deploymentExecutor: executor);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("service", run.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("7443", run.Output, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed", run.Output, StringComparison.Ordinal);
        Assert.Empty(executor.Applied);
    }

    /// Rule 3: no mutating operation without explicit typed confirmation. A wrong answer
    /// leaves the host untouched.
    [Fact]
    public async Task Service_changes_nothing_until_the_node_name_is_typed()
    {
        FakeDeploymentExecutor executor = new();

        CliRun run = await Run(
            ["service", "install"], deploymentExecutor: executor, typed: "yes");

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Empty(executor.Applied);
        Assert.Contains("nothing was changed", run.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Service_applies_every_step_once_confirmed()
    {
        FakeDeploymentExecutor executor = new();

        CliRun run = await Run(
            ["service", "install"],
            deploymentExecutor: executor,
            typed: FakeScenarios.LocalHostName);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Equal(
            [DeploymentAction.CreateService, DeploymentAction.CreateFirewallRule,
             DeploymentAction.GrantSnapshotAccess, DeploymentAction.GrantLogsAccess,
             DeploymentAction.RegisterEventSource, DeploymentAction.StartService],
            executor.Applied);
    }

    /// The confirmation is case-insensitive: host names are, and forcing the operator to match
    /// case at 3 a.m. buys nothing.
    [Fact]
    public async Task The_confirmation_accepts_the_node_name_in_any_case()
    {
        FakeDeploymentExecutor executor = new();

        await Run(
            ["service", "install"],
            deploymentExecutor: executor,
            typed: FakeScenarios.LocalHostName.ToLowerInvariant());

        Assert.NotEmpty(executor.Applied);
    }

    [Fact]
    public async Task Service_remove_undoes_the_deployment_in_reverse()
    {
        FakeDeploymentExecutor executor = new(Deployed());

        CliRun run = await Run(
            ["service", "remove"],
            deploymentExecutor: executor,
            typed: FakeScenarios.LocalHostName);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Equal(
            [DeploymentAction.RemoveEventSource, DeploymentAction.RevokeLogsAccess,
             DeploymentAction.RevokeSnapshotAccess, DeploymentAction.RemoveFirewallRule,
             DeploymentAction.RemoveService],
            executor.Applied);
    }

    /// A plan that stopped part-way left the service installed and the port shut. That is
    /// exit code 5 — not 3, which says the tool could not read the host and implies the host
    /// is where it was.
    [Fact]
    public async Task Service_that_fails_after_a_step_reports_an_intermediate_state()
    {
        FakeDeploymentExecutor executor = new(failOnStep: 1);

        CliRun run = await Run(
            ["service", "install"], deploymentExecutor: executor, typed: FakeScenarios.LocalHostName);

        Assert.Equal(ExitCode.IntermediateState, run.Code);
        Assert.Single(executor.Applied);
        Assert.Contains("intermediate state", run.Output, StringComparison.Ordinal);
    }

    /// Failing on the first step is the other case entirely: nothing was applied, so nothing
    /// is half-done, and sending the operator to look for damage would be a wrong answer.
    [Fact]
    public async Task Service_that_fails_on_the_first_step_says_nothing_changed()
    {
        FakeDeploymentExecutor executor = new(failOnStep: 0);

        CliRun run = await Run(
            ["service", "install"], deploymentExecutor: executor, typed: FakeScenarios.LocalHostName);

        Assert.Equal(ExitCode.LocalAccessFailure, run.Code);
        Assert.Empty(executor.Applied);
        Assert.Contains("Nothing was changed", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("intermediate state", run.Output, StringComparison.Ordinal);
    }

    /// The field failure: `sc start` came back with 1053. The console has no room for the
    /// reason, so it names the read-only command that shows it.
    [Fact]
    public async Task Service_install_whose_start_fails_points_to_ripcord_service()
    {
        FakeDeploymentExecutor executor = new(failOnStep: 5);

        CliRun run = await Run(
            ["service", "install"], deploymentExecutor: executor, typed: FakeScenarios.LocalHostName);

        Assert.Equal(DeploymentAction.RegisterEventSource, executor.Applied[^1]);
        Assert.Contains(
            "Run 'ripcord service' to see why it did not start.", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_install_whose_firewall_step_fails_does_not_blame_the_start()
    {
        FakeDeploymentExecutor executor = new(failOnStep: 1);

        CliRun run = await Run(
            ["service", "install"], deploymentExecutor: executor, typed: FakeScenarios.LocalHostName);

        Assert.DoesNotContain("see why it did not start", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_says_when_the_service_account_cannot_write_its_logs()
    {
        CliRun run = await Run(
            ["service"],
            deploymentExecutor: new FakeDeploymentExecutor(
                Deployed() with { LogsWritableByService = false }));

        Assert.Contains(
            @"logs       NOT writable by NT SERVICE\ripcord", run.Output, StringComparison.Ordinal);
        Assert.Contains("ripcord service install --dry-run", run.Output, StringComparison.Ordinal);
    }

    /// `status` is the word an operator types by habit: it is the same report, not a second one.
    [Fact]
    public async Task Service_status_is_the_same_report_as_bare_service()
    {
        FakeDeploymentExecutor executor = new(Deployed());

        CliRun bare = await Run(["service"], deploymentExecutor: executor);
        CliRun status = await Run(["service", "status"], deploymentExecutor: executor);

        Assert.Equal(ExitCode.Success, status.Code);
        Assert.Equal(bare.Output, status.Output);
        Assert.Empty(executor.Applied);
    }

    [Fact]
    public async Task A_word_that_is_not_a_service_verb_points_to_bare_service()
    {
        CliRun run = await Run(["service", "state"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Equal(
            "ripcord: 'service state' is not a service verb.\n"
                + "  to look:    ripcord service\n"
                + "  to change:  ripcord service install | remove | restart\n",
            run.Error.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Service_on_a_stopped_listener_prints_the_last_exit_the_log_and_why()
    {
        StubLogReader logs = new(
        [
            .. ListenerStartup.Banner("0.4.1", "ripcord.yaml").Render(Now),
            .. CommandEntries.Exited("serve", ExitCode.InvalidConfiguration).Render(Now),
        ]);

        CliRun run = await Run(
            ["service", "status"],
            deploymentExecutor: new FakeDeploymentExecutor(
                Deployed() with { ServiceRunning = false },
                service: new ObservedService(
                    true,
                    $"\"{BinaryPath}\" serve",
                    ServiceRunState.Stopped,
                    "Auto",
                    ServiceExitCode.ToWindows(ExitCode.InvalidConfiguration),
                    0)),
            logReader: logs);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("service    STOPPED      start mode Auto", run.Output, StringComparison.Ordinal);
        Assert.Contains($"command    \"{BinaryPath}\" serve", run.Output, StringComparison.Ordinal);
        Assert.Contains(
            "last exit  0x20000002, Ripcord exit 2: the configuration did not load",
            run.Output,
            StringComparison.Ordinal);
        Assert.Contains("LAST 3 LINES OF THE LOG", run.Output, StringComparison.Ordinal);
        Assert.Contains("14:00:00.000Z serve: exit 2: InvalidConfiguration", run.Output, StringComparison.Ordinal);
        Assert.Contains(
            "Why it is not running:\n    it stopped with exit 2: the configuration did not load\n"
            + "  Then run:\n    ripcord check\n",
            run.Output,
            StringComparison.Ordinal);

        // The service's own folder, today's file first.
        Assert.EndsWith("listener-2026-09-12.log", logs.Asked[0], StringComparison.Ordinal);
    }

    /// The likeliest reason the listener stopped is a configuration that does not load. The
    /// service is still shown, and the exit code is still the configuration's.
    [Fact]
    public async Task Service_with_an_unusable_configuration_still_shows_the_service()
    {
        CliRun run = await Run(
            ["service"],
            configStore: new MemoryConfigStore(
                ConfigurationRead.Failed("listener.port", "not a number")),
            deploymentExecutor: new FakeDeploymentExecutor(
                service: new ObservedService(
                    true, $"\"{BinaryPath}\" serve", ServiceRunState.Stopped, "Auto", 0, 0)));

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("service    STOPPED", run.Output, StringComparison.Ordinal);
        Assert.Contains("Firewall, snapshot and logs access are not shown", run.Output, StringComparison.Ordinal);
        Assert.Contains("Why it is not running:\n    no log today or yesterday", run.Output, StringComparison.Ordinal);
        Assert.Contains("Get-WinEvent -ProviderName ripcord", run.Output, StringComparison.Ordinal);
        Assert.Contains("listener.port: not a number", run.Error, StringComparison.Ordinal);
    }

    /// Read on a 1024x768 KVM, with a long install path and a long log line.
    [Fact]
    public async Task Service_on_a_stopped_listener_with_long_lines_fits_75_columns()
    {
        StubLogReader logs = new(
        [
            .. ListenerStartup.Banner("0.4.1", "ripcord.yaml").Render(Now),
            DiagnosticEntry.Of("serve", new string('x', 200)).Render(Now)[0],
            .. CommandEntries.Exited("serve", ExitCode.LocalAccessFailure).Render(Now),
        ]);

        CliRun run = await Run(
            ["service"],
            deploymentExecutor: new FakeDeploymentExecutor(
                Deployed() with { ServiceRunning = false },
                service: new ObservedService(
                    true,
                    "\"C:\\Program Files\\Ripcord Disaster Recovery\\bin\\ripcord.exe\" serve",
                    ServiceRunState.Stopped,
                    "Auto",
                    ServiceExitCode.ToWindows(ExitCode.LocalAccessFailure),
                    0)),
            logReader: logs);

        string[] lines = run.Output.Split('\n');

        Assert.True(lines.Length > 20, run.Output);
        Assert.All(lines, line => Assert.True(line.Length <= 75, line));
    }

    /// Rule 4: the two new deployment steps are in the dry run, and the dry run changes
    /// nothing.
    [Fact]
    public async Task Service_install_dry_run_lists_the_logs_grant_and_the_event_source()
    {
        FakeDeploymentExecutor executor = new();

        CliRun run = await Run(["service", "install", "--dry-run"], deploymentExecutor: executor);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains(
            @"grant NT SERVICE\ripcord modify access", run.Output, StringComparison.Ordinal);
        Assert.Contains(
            "Register the 'ripcord' source in the Application event log",
            run.Output,
            StringComparison.Ordinal);
        Assert.Empty(executor.Applied);
    }

    /// The one move that follows every configuration edit. The listener reads `ripcord.yaml`
    /// once, when it starts, so an edited file changes nothing until this has run.
    [Fact]
    public async Task Service_restart_stops_and_starts_a_running_listener()
    {
        FakeDeploymentExecutor executor = new(Deployed());

        CliRun run = await Run(["service", "restart"], deploymentExecutor: executor);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Equal([DeploymentAction.RestartService], executor.Applied);

        // Under its own heading: a restart printed under "DEPLOYMENT" is the kind of small lie
        // that costs a second of doubt on the one screen that is read under pressure.
        Assert.Contains("RIPCORD LISTENER RESTART", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("DEPLOYMENT", run.Output, StringComparison.Ordinal);
    }

    /// `sc stop` on a stopped service is an error, and an operator asking for the
    /// configuration to take effect means the same thing either way.
    [Fact]
    public async Task Service_restart_starts_a_listener_that_was_not_running()
    {
        FakeDeploymentExecutor executor = new(Deployed() with { ServiceRunning = false });

        CliRun run = await Run(["service", "restart"], deploymentExecutor: executor);

        Assert.Equal([DeploymentAction.StartService], executor.Applied);
    }

    [Fact]
    public async Task Service_restart_on_a_host_with_no_service_says_so_and_does_nothing()
    {
        FakeDeploymentExecutor executor = new();

        CliRun run = await Run(["service", "restart"], deploymentExecutor: executor);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Empty(executor.Applied);
        Assert.Contains("no listener service", run.Error, StringComparison.Ordinal);
    }

    /// It mutates, so it rehearses — and the rehearsal changes nothing.
    [Fact]
    public async Task Service_restart_dry_run_changes_nothing()
    {
        FakeDeploymentExecutor executor = new(Deployed());

        CliRun run = await Run(["service", "restart", "--dry-run"], deploymentExecutor: executor);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Empty(executor.Applied);
        Assert.Contains("Nothing was changed", run.Output, StringComparison.Ordinal);
    }

    /// The command is a noun, and bare it changes nothing. Rule 3: an operator looking at a
    /// host must not be one keystroke from installing a service on it.
    [Fact]
    public async Task Service_on_its_own_reports_and_changes_nothing()
    {
        FakeDeploymentExecutor executor = new(Deployed());

        CliRun run = await Run(["service"], deploymentExecutor: executor);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("RIPCORD LISTENER", run.Output, StringComparison.Ordinal);
        Assert.Contains("service    running", run.Output, StringComparison.Ordinal);
        Assert.Contains("It matches the configuration", run.Output, StringComparison.Ordinal);
        Assert.Empty(executor.Applied);
    }

    /// It says what is missing by naming the command that would show it, rather than printing
    /// a plan — a plan printed by a command that changes nothing reads like one that is about
    /// to.
    [Fact]
    public async Task Service_on_a_bare_host_names_the_command_that_would_change_it()
    {
        CliRun run = await Run(["service"]);

        Assert.Contains("service    not installed", run.Output, StringComparison.Ordinal);
        Assert.Contains(
            "ripcord service install --dry-run", run.Output, StringComparison.Ordinal);
    }

    /// Neither `install` nor `remove` is needed to look, and a word that is neither is refused
    /// rather than read as one of them.
    [Theory]
    [InlineData("start")]
    [InlineData("deploy")]
    [InlineData("instal")]
    public async Task Service_refuses_a_word_that_is_not_one_of_the_two(string word)
    {
        FakeDeploymentExecutor executor = new();

        CliRun run = await Run(["service", word], deploymentExecutor: executor);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Empty(executor.Applied);
    }

    /// `--remove` was a flag on the old verb. It is a word now, and the flag must not linger
    /// as something that parses and does nothing.
    [Fact]
    public async Task Service_install_refuses_the_old_remove_flag()
    {
        CliRun run = await Run(["service", "install", "--remove"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("unexpected argument", run.Error, StringComparison.Ordinal);
    }

    /// The question an operator asks first, and which nothing answered: is the listener
    /// actually running? Installed and running are two facts — a registered service that is
    /// stopped serves nothing, and the peer then reports this pair offline, which reads as a
    /// network fault rather than as a service somebody has to start.
    [Fact]
    public async Task Service_says_what_is_on_the_host_before_what_would_change()
    {
        CliRun run = await Run(
            ["service", "install", "--dry-run"], deploymentExecutor: new FakeDeploymentExecutor(Deployed()));

        Assert.Contains("ON THIS HOST", run.Output, StringComparison.Ordinal);
        Assert.Contains("service    running", run.Output, StringComparison.Ordinal);

        // The command line with it: a second copy of the binary in another directory is how a
        // pair ends up running two versions of the sequences.
        Assert.Contains($"{BinaryPath} serve", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_says_so_when_the_service_is_installed_and_stopped()
    {
        CliRun run = await Run(
            ["service", "install", "--dry-run"],
            deploymentExecutor: new FakeDeploymentExecutor(
                Deployed() with { ServiceRunning = false }));

        Assert.Contains("service    STOPPED", run.Output, StringComparison.Ordinal);
        Assert.Contains("Start the 'ripcord' service", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_on_a_bare_host_says_the_service_is_not_installed()
    {
        CliRun run = await Run(["service", "install", "--dry-run"]);

        Assert.Contains("service    not installed", run.Output, StringComparison.Ordinal);
    }

    /// Re-running a correct deployment must be safe and obviously uneventful.
    [Fact]
    public async Task Service_on_an_already_correct_host_does_nothing()
    {
        FakeDeploymentExecutor executor = new(Deployed());

        CliRun run = await Run(["service", "install"], deploymentExecutor: executor);

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

        CliRun run = await Run(["service", "install", "--dryrun"], deploymentExecutor: executor);

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

    [Fact]
    public async Task Service_names_the_snapshot_and_says_what_it_is()
    {
        CliRun run = await Run(["service"], deploymentExecutor: new FakeDeploymentExecutor(Deployed()));

        Assert.Contains(@"snapshot   D:\Ripcord\state.json", run.Output, StringComparison.Ordinal);
        Assert.Contains(
            "written by 'ripcord status', served to the peer", run.Output, StringComparison.Ordinal);
        Assert.Contains(@"readable by NT SERVICE\ripcord", run.Output, StringComparison.Ordinal);
    }

    /// The grant used to fail third, after the service and the port were created. Refused
    /// before the prompt, so nothing is asked and nothing changes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_install_is_refused_before_any_prompt_when_the_snapshot_volume_is_absent(
        bool dryRun)
    {
        FakeDeploymentExecutor executor = new(
            ObservedDeployment.Nothing with { SnapshotVolumePresent = false });

        CliRun run = await Run(
            dryRun ? ["service", "install", "--dry-run"] : ["service", "install"],
            deploymentExecutor: executor,
            typed: FakeScenarios.LocalHostName);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Empty(executor.Applied);
        Assert.Contains("Cannot be installed as configured", run.Output, StringComparison.Ordinal);
        Assert.Contains("the old default", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("This creates a Windows service", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_on_its_own_says_why_it_cannot_be_installed()
    {
        CliRun run = await Run(
            ["service"],
            deploymentExecutor: new FakeDeploymentExecutor(
                ObservedDeployment.Nothing with { SnapshotVolumePresent = false }));

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("Cannot be installed as configured", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("step(s) would change it", run.Output, StringComparison.Ordinal);
    }

    /// Rule 6: a 1024x768 KVM console.
    [Theory]
    [InlineData("service")]
    [InlineData("service install --dry-run")]
    public async Task Service_output_fits_75_columns(string command)
    {
        CliRun run = await Run(command.Split(' '));

        Assert.All(run.Output.Split('\n'), line => Assert.True(line.Length <= 75, line));
    }

    private const string ProgramFilesBinary = @"C:\Program Files\Ripcord\ripcord.exe";

    /// The lines read when an install half-fails, with the path a real host has.
    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task Applied_service_install_fits_75_columns(int failOnStep)
    {
        CliRun run = await Run(
            ["service", "install"],
            deploymentExecutor: new FakeDeploymentExecutor(
                failOnStep: failOnStep,
                failure: "'icacls \"C:\\Program Files\\Ripcord\\logs\" /grant \"NT SERVICE\\ripcord\"'"
                    + " exited with 1332: No mapping between account names and security IDs "
                    + "was done."),
            typed: FakeScenarios.LocalHostName,
            binaryPath: ProgramFilesBinary);

        // On a console the typed answer ends the prompt's line; here nothing does.
        string output = run.Output.Replace(
            $"({FakeScenarios.LocalHostName}):  ", "\n", StringComparison.Ordinal);

        Assert.Contains("done: Create the 'ripcord' service", output, StringComparison.Ordinal);
        Assert.All(output.Split('\n'), line => Assert.True(line.Length <= 75, line));
    }

    /// The documented off switch has to leave a way to look at, and take off, a service
    /// installed while it was on.
    [Fact]
    public async Task Service_on_a_disabled_listener_reports_and_exits_zero()
    {
        CliRun run = await Run(
            ["service"],
            configStore: new RecordingConfigStore(listenerEnabled: false),
            deploymentExecutor: new FakeDeploymentExecutor(Deployed() with { ServiceRunning = false }));

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains(
            "the listener is disabled in ripcord.yaml (listener.enabled: false)",
            run.Output,
            StringComparison.Ordinal);
        Assert.Contains("    ripcord service remove", run.Output, StringComparison.Ordinal);
        Assert.Empty(run.Error);
    }

    [Fact]
    public async Task Restart_on_a_disabled_listener_refuses_and_names_the_way_out()
    {
        FakeDeploymentExecutor executor = new(Deployed() with { ServiceRunning = false });

        CliRun run = await Run(
            ["service", "restart"],
            configStore: new RecordingConfigStore(listenerEnabled: false),
            deploymentExecutor: executor);

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Contains("listener.enabled: true", run.Error, StringComparison.Ordinal);
        Assert.Contains("ripcord service remove", run.Error, StringComparison.Ordinal);
        Assert.Empty(executor.Applied);
    }

    [Fact]
    public async Task Remove_on_a_disabled_listener_still_removes()
    {
        FakeDeploymentExecutor executor = new(Deployed());

        CliRun run = await Run(
            ["service", "remove"],
            configStore: new RecordingConfigStore(listenerEnabled: false),
            deploymentExecutor: executor,
            typed: FakeScenarios.LocalHostName);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains(DeploymentAction.RemoveService, executor.Applied);
    }

    [Fact]
    public async Task Install_on_a_disabled_listener_is_blocked_and_changes_nothing()
    {
        FakeDeploymentExecutor executor = new();

        CliRun run = await Run(
            ["service", "install"],
            configStore: new RecordingConfigStore(listenerEnabled: false),
            deploymentExecutor: executor,
            typed: FakeScenarios.LocalHostName);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("listener.enabled: false", run.Output, StringComparison.Ordinal);
        Assert.Empty(executor.Applied);
    }

    [Fact]
    public async Task An_unknown_service_state_is_not_called_not_running()
    {
        CliRun run = await Run(
            ["service"],
            deploymentExecutor: new FakeDeploymentExecutor(
                Deployed(),
                service: new ObservedService(
                    true,
                    $"\"{BinaryPath}\" serve",
                    ServiceRunState.Unknown,
                    null,
                    null,
                    null,
                    ObservedService.AccessDenied)));

        Assert.DoesNotContain("Why it is not running", run.Output, StringComparison.Ordinal);
        Assert.Contains("  Note:", run.Output, StringComparison.Ordinal);
        Assert.Contains("elevated console", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_says_when_the_snapshot_was_never_written_and_names_status()
    {
        CliRun run = await Run(["service"], deploymentExecutor: new FakeDeploymentExecutor(Deployed()));

        Assert.Contains("               NOT written yet\n", run.Output, StringComparison.Ordinal);
        Assert.EndsWith(
            "  The peer is served no current snapshot until this runs:\n    ripcord status\n",
            run.Output.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_gives_the_age_of_a_fresh_snapshot_and_no_status_hint()
    {
        CliRun run = await Run(
            ["service"],
            deploymentExecutor: new FakeDeploymentExecutor(
                Deployed() with { SnapshotWrittenAt = Now.AddSeconds(-30) }));

        Assert.Contains("               written 30s ago\n", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ripcord status\n", run.Output, StringComparison.Ordinal);
    }

    private static ObservedDeployment Deployed() => new(
        ServiceInstalled: true,
        ServiceBinaryPath: BinaryPath,
        FirewallRuleInstalled: true,
        FirewallPort: 7443,
        FirewallRemoteAddress: "192.0.2.11",
        SnapshotReadableByService: true,
        ServiceRunning: true,
        LogsWritableByService: true,
        EventSourceRegistered: true);

    private const string DefaultConfigPath = "/opt/ripcord/ripcord.yaml";

    private const string BinaryPath = "/opt/ripcord/ripcord";

    /// Off unless the configuration switches it on, and loud about it: a host somebody
    /// believes is checking for updates and silently is not is the worse of the two failures.
    [Fact]
    public async Task Check_update_refuses_on_a_host_that_did_not_ask_for_it()
    {
        CliRun run = await Run(["check-update"]);

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("switched off", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_update_reports_a_newer_release()
    {
        CliRun run = await Run(
            ["check-update"],
            configStore: new RecordingConfigStore(updates: true),
            releaseFeed: StubReleaseFeed.Publishing("99.0.0"));

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("An update is available", run.Output, StringComparison.Ordinal);
        Assert.Contains("99.0.0", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_update_on_the_current_release_says_so_and_exits_zero()
    {
        CliRun run = await Run(
            ["check-update"],
            configStore: new RecordingConfigStore(updates: true),
            releaseFeed: StubReleaseFeed.Publishing(BuildInfo.Version));

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("the latest release", run.Output, StringComparison.Ordinal);
    }

    /// A host with no outbound access is the design. It must say it could not look, and it
    /// must never say it is up to date.
    [Fact]
    public async Task Check_update_that_cannot_reach_github_says_so_rather_than_guessing()
    {
        CliRun run = await Run(
            ["check-update"],
            configStore: new RecordingConfigStore(updates: true),
            releaseFeed: StubReleaseFeed.Unreachable());

        Assert.Equal(ExitCode.LocalAccessFailure, run.Code);
        Assert.Contains("could not be resolved", run.Error, StringComparison.Ordinal);
        Assert.Empty(run.Output);
    }

    /// The page is served on the loopback interface and nowhere else, so the command prints
    /// the address it is on: an operator who has to guess it will type the host name.
    [Fact]
    public async Task Dashboard_serves_the_page_on_the_loopback_interface()
    {
        CapturingDashboardServer server = new();

        CliRun run = await Run(
            ["dashboard"],
            configStore: new RecordingConfigStore(dashboard: true),
            dashboardServer: server);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("127.0.0.1:7080", run.Output, StringComparison.Ordinal);
        Assert.NotNull(server.Served);
        Assert.True(server.Served!.Enabled);
    }

    [Fact]
    public async Task Dashboard_builds_a_page_carrying_the_state_of_the_pair()
    {
        CapturingDashboardServer server = new();

        await Run(
            ["dashboard"],
            configStore: new RecordingConfigStore(dashboard: true),
            dashboardServer: server);

        Assert.StartsWith("<!DOCTYPE html>", server.Page!, StringComparison.Ordinal);
        Assert.Contains(FakeScenarios.LocalHostName, server.Page!, StringComparison.Ordinal);
    }

    /// Off is the ordinary state. A node asked to serve a page it does not serve says so and
    /// exits cleanly, the same degradation `serve` makes with the listener switched off.
    [Fact]
    public async Task Dashboard_on_a_node_that_serves_no_page_says_so_and_serves_nothing()
    {
        CapturingDashboardServer server = new();

        CliRun run = await Run(["dashboard"], dashboardServer: server);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("disabled on this node", run.Error, StringComparison.Ordinal);
        Assert.Null(server.Served);
    }

    /// There is no stderr anybody is watching and no exit code to read: a host that cannot be
    /// read has to say so on the page itself.
    [Fact]
    public async Task Dashboard_renders_a_page_saying_why_when_the_host_cannot_be_read()
    {
        CapturingDashboardServer server = new();

        await Run(
            ["dashboard"],
            configStore: new RecordingConfigStore(dashboard: true),
            provider: new UnreadableHypervProvider(),
            dashboardServer: server);

        Assert.StartsWith("<!DOCTYPE html>", server.Page!, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN", server.Page!, StringComparison.Ordinal);
        Assert.Contains("WMI refused the query", server.Page!, StringComparison.Ordinal);
    }

    /// Nothing on these hosts looks for a release on its own, and no command looks while it
    /// runs — a fifteen-second timeout on a host with no outbound access, in front of a
    /// command somebody typed during an incident, is the thing this design exists to avoid.
    /// So `check-update` writes down what it found, and everything else reads that.
    [Fact]
    public async Task Check_update_writes_down_what_it_found()
    {
        MemoryUpdateNoticeStore notices = new();

        await Run(
            ["check-update"],
            configStore: new RecordingConfigStore(updates: true),
            releaseFeed: StubReleaseFeed.Publishing("9.9.9"),
            updateNotices: notices);

        Assert.Equal("9.9.9", notices.Notice!.Version);
    }

    [Fact]
    public async Task Status_mentions_a_release_somebody_looked_for_earlier()
    {
        CliRun run = await Run(
            ["status"],
            updateNotices: new MemoryUpdateNoticeStore(new UpdateNotice("9.9.9", Now.AddDays(-1))));

        Assert.Contains("9.9.9 is available", run.Output, StringComparison.Ordinal);
        Assert.Contains("ripcord update", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_mentions_it_too()
    {
        CliRun run = await Run(
            ["check"],
            updateNotices: new MemoryUpdateNoticeStore(new UpdateNotice("9.9.9", Now.AddDays(-1))));

        Assert.Contains("9.9.9 is available", run.Output, StringComparison.Ordinal);
    }

    /// A host that has never looked says nothing at all, which is every host until somebody
    /// switches `updates.check` on and schedules it.
    [Fact]
    public async Task A_host_that_never_looked_says_nothing_about_updates()
    {
        CliRun run = await Run(["status"]);

        Assert.DoesNotContain("is available", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("ripcord update", run.Output, StringComparison.Ordinal);
    }

    /// The file still names the release this host just installed. Repeating it would send
    /// somebody to run an update that has already happened.
    [Fact]
    public async Task A_release_this_host_already_runs_is_not_mentioned()
    {
        CliRun run = await Run(
            ["status"],
            updateNotices: new MemoryUpdateNoticeStore(
                new UpdateNotice(BuildInfo.Version, Now.AddDays(-1))));

        Assert.DoesNotContain("is available", run.Output, StringComparison.Ordinal);
    }

    /// What the log is for. The console keeps its one sentence for the KVM; the file keeps
    /// what was run, what it exited with, and — the part that cannot be retyped from a
    /// photograph of a screen — the exception underneath.
    [Fact]
    public async Task Every_command_records_what_was_run_and_what_it_exited_with()
    {
        RecordingDiagnosticLog log = new();

        await Run(["status"], diagnostics: log);

        Assert.Equal(
            ["running: status", "exit 0: Success"],
            log.Written.Select(entry => entry.Message));

        Assert.All(log.Written, entry => Assert.Equal("status", entry.Operation));
    }

    /// The sentence on the console is one translated clause with no class and no stack. The
    /// log gets the exception, which is the difference between an operator who can send
    /// something and one who can only describe it.
    [Fact]
    public async Task A_host_that_cannot_be_read_puts_the_exception_in_the_log()
    {
        RecordingDiagnosticLog log = new();

        CliRun run = await Run(
            ["status"], provider: new UnreadableHypervProvider(), diagnostics: log);

        Assert.Equal(ExitCode.LocalAccessFailure, run.Code);

        DiagnosticEntry failure = Assert.Single(
            log.Written, entry => entry.Operation == "hyper-v");

        // The type and the stack: neither is in the sentence the console prints, and both are
        // what a failure that only happens on one host is diagnosed from.
        Assert.Contains(
            failure.Detail,
            line => line.Contains("InvalidOperationException", StringComparison.Ordinal));

        Assert.Contains(
            failure.Detail,
            line => line.Contains("at Ripcord.Application.LocalStateReader", StringComparison.Ordinal));
    }

    /// The path is in the file, and the file is the thing most likely to be wrong. So the log
    /// is pointed at it before anything is validated, and stays in the logs folder beside the
    /// binary when the section is absent.
    [Fact]
    public async Task The_log_goes_where_the_configuration_asks()
    {
        RecordingDiagnosticLog log = new();

        await Run(["status"], diagnostics: log);

        // Stated as the rule — the log sits in the logs folder beside the binary — rather than
        // as a literal path. `Path.Combine` writes a backslash on Windows, and the release
        // workflow runs this same suite on windows-latest, so a hard-coded POSIX path passed
        // locally and failed the moment a release was rehearsed.
        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(BinaryPath) ?? "", "logs"),
            log.Destination?.Folder);

        Assert.True(log.Destination?.Enabled);
    }

    private static async Task<CliRun> Run(
        string[] args,
        string machineName = FakeScenarios.LocalHostName,
        IHypervProvider? provider = null,
        IConfigStore? configStore = null,
        IDeploymentExecutor? deploymentExecutor = null,
        string? typed = null,
        INotifier? notifier = null,
        IAlertStateStore? alertState = null,
        IReleaseFeed? releaseFeed = null,
        IDashboardServer? dashboardServer = null,
        IReleaseSource? releaseSource = null,
        IUpdateNoticeStore? updateNotices = null,
        IBinarySwap? binarySwap = null,
        IDiagnosticLog? diagnostics = null,
        IDiagnosticLogReader? logReader = null,
        string binaryPath = BinaryPath,
        CancellationToken cancellationToken = default)
    {
        StringWriter output = new();
        StringWriter error = new();

        RipcordCli cli = new(
            new RipcordPorts(
                configStore ?? new RecordingConfigStore(),
                provider ?? new FakeHypervProvider(FakeScenarios.Healthy(Now)),
                FakeHostSystemProvider.Target(),
                FakeCertificateProvider.Valid(
                    Tests.Configuration.ValidDocument.LocalThumbprint, "CN=HV-REPLICA-01"),
                FakePeerChannel.Absent(),
                new InMemorySnapshotStore(),
                deploymentExecutor ?? new FakeDeploymentExecutor(),
                new NoOpPeerListener(),
                dashboardServer ?? new NoOpDashboardServer(),
                new InMemoryAuditLog(),
                notifier ?? new StubNotifier(),
                alertState ?? new MemoryAlertStateStore(),
                releaseFeed ?? StubReleaseFeed.Unreachable(),
                releaseSource ?? new NoReleaseSource(),
                updateNotices ?? new MemoryUpdateNoticeStore(),
                binarySwap ?? new NoBinarySwap(),
                new FixedClock(Now),
                diagnostics ?? new SilentDiagnosticLog(),
                logReader ?? new NoLogs()),
            new CliEnvironment(
                machineName, DefaultConfigPath, binaryPath, new StringReader(typed ?? "")));

        ExitCode code = await cli.RunAsync(args, output, error, cancellationToken);

        return new CliRun(code, output.ToString(), error.ToString());
    }

    private sealed record CliRun(ExitCode Code, string Output, string Error);

    /// Holds one day's listener log, whatever file is asked for, and records which were.
    private sealed class StubLogReader(IReadOnlyList<string> lines) : IDiagnosticLogReader
    {
        public List<string> Asked { get; } = [];

        public LogReading? Tail(string path, int maxLines)
        {
            this.Asked.Add(path);
            return new LogReading(path, [.. lines.TakeLast(maxLines)], null);
        }
    }

    private sealed class UnreadableHypervProvider : ReadOnlyHypervProvider
    {
        public override Task<HostState> GetLocalStateAsync(CancellationToken cancellationToken) =>
            Task.FromException<HostState>(new InvalidOperationException("WMI refused the query"));
    }

    private sealed class CancellingProvider : ReadOnlyHypervProvider
    {
        public override Task<HostState> GetLocalStateAsync(CancellationToken cancellationToken) =>
            Task.FromCanceled<HostState>(
                cancellationToken.IsCancellationRequested ? cancellationToken : new(true));
    }

    /// Reports a bare host, and records what it was asked to change.
    private sealed class FakeDeploymentExecutor(
        ObservedDeployment? observed = null,
        int failOnStep = -1,
        ObservedService? service = null,
        string failure = "sc.exe exited with code 5") : IDeploymentExecutor
    {
        private readonly List<DeploymentAction> applied = [];

        public IReadOnlyList<DeploymentAction> Applied => this.applied;

        public ObservedDeployment Observe(DesiredDeployment desired, ObservedService service) =>
            observed ?? ObservedDeployment.Nothing;

        /// Agrees with `Observe` unless a test says otherwise.
        public ObservedService ObserveService() =>
            service ?? (observed is { ServiceInstalled: true } deployed
                ? new ObservedService(
                    true,
                    $"\"{deployed.ServiceBinaryPath}\" serve",
                    deployed.ServiceRunning ? ServiceRunState.Running : ServiceRunState.Stopped,
                    "Auto",
                    0,
                    0)
                : ObservedService.Absent);

        public void Apply(DeploymentStep change, DesiredDeployment desired)
        {
            if (this.applied.Count == failOnStep)
            {
                throw new InvalidOperationException(failure);
            }

            this.applied.Add(change.Action);
        }
    }



    /// Returns a document the validator accepts, and remembers which path was asked for.
    private sealed class RecordingConfigStore(
        bool listenerEnabled = true,
        bool alerting = false,
        bool updates = false,
        bool dashboard = false) : ReadOnlyConfigStore
    {
        public string? RequestedPath { get; private set; }

        public override ConfigurationRead Read(string path)
        {
            this.RequestedPath = path;
            ConfigurationDocument document = Tests.Configuration.ValidDocument.Create();
            document.Listener!.Enabled = listenerEnabled;
            document.Listener.SnapshotPath = @"D:\Ripcord\state.json";

            if (alerting)
            {
                document.Alerting = new AlertingDocument
                {
                    Enabled = true,
                    Smtp = new SmtpDocument
                    {
                        Host = "smtp.example.net",
                        From = "ripcord@example.net",
                        To = ["ops@example.net"],
                    },
                };
            }

            if (updates)
            {
                document.Updates = new UpdatesDocument { Check = true };
            }

            if (dashboard)
            {
                document.Dashboard = new DashboardDocument { Enabled = true };
            }

            return ConfigurationRead.Succeeded(document);
        }
    }
}
