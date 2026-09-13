using Ripcord.Application.Checks;
using Ripcord.Application.Deployment;
using Ripcord.Application.Failover;
using Ripcord.Application.Status;
using Ripcord.Application.TestFailover;
using Ripcord.Application;
using Ripcord.Cli.Rendering;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Pairing;
using Ripcord.Ports.Deployment;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Failover;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Hosts;
using Ripcord.Ports.Pairing;
using Ripcord.Ports.Audit;
using Ripcord.Ports.Replication;
using Ripcord.Ports;

namespace Ripcord.Cli;

/// What the composition root knows about the machine. Passed in rather than read here so the
/// whole command surface is exercisable from the Linux test container.
public sealed record CliEnvironment(
    string MachineName,
    string DefaultConfigurationPath,
    string BinaryPath,
    TextReader? ConfirmationReader = null,
    /// Who is running the command. It goes in the audit trail (D8), which is read afterwards by
    /// somebody working out who moved production and what they knew at the time.
    string UserName = "unknown",
    /// Which binary this is. Normally the one that is running, and injectable so a test can
    /// put two known builds on the two sides of the pair rather than assert against whatever
    /// the test host happened to stamp.
    BuildIdentity? Build = null);

/// Argument parsing and console rendering. No decision lives here: the exit code comes from
/// the use case, the layout from StatusRenderer.
public sealed class RipcordCli(
    IConfigStore configStore,
    IHypervProvider provider,
    IHostSystemProvider hostSystemProvider,
    ICertificateProvider certificateProvider,
    IPeerChannel peerChannel,
    ISnapshotStore snapshotStore,
    IDeploymentExecutor deploymentExecutor,
    IPeerListener peerListener,
    IAuditLog audit,
    IClock clock,
    CliEnvironment environment)
{
    /// Typed in full, not "y": this creates a Windows service and opens an inbound port on a
    /// host that may run a domain controller. A keystroke is not a decision.
    private const string ConfirmationPrompt = "Type the node name to confirm: ";

    public async Task<ExitCode> RunAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            WriteUsage(output);
            return ExitCode.Success;
        }

        try
        {
            return await this.DispatchAsync(args, output, error, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C. A stack trace is not an answer, and the read had not changed anything.
            error.WriteLine("ripcord: cancelled.");
            return ExitCode.LocalAccessFailure;
        }
    }

    private async Task<ExitCode> DispatchAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        switch (args[0])
        {
            case "status":
                return await this.StatusAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "check":
                return await this.CheckAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "test-failover":
                return await this.TestFailoverAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "failover":
                return await this.FailoverAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "failback":
                return await this.FailoverAsync(
                        args[1..], output, error, cancellationToken,
                        FailoverOperation.Failback)
                    .ConfigureAwait(false);

            case "fence":
                return await this.FenceAsync(args[1..], output, error, cancellationToken)
                    .ConfigureAwait(false);

            case "serve":
                return await this.ServeAsync(args[1..], error, cancellationToken)
                    .ConfigureAwait(false);

            case "deploy-listener":
                return this.DeployListener(args[1..], output, error);

            case "version":
                output.WriteLine($"ripcord {BuildInfo.VersionWithCommit}");
                return ExitCode.Success;

            default:
                error.WriteLine($"ripcord: unknown command '{args[0]}'.");
                WriteUsage(error);
                return ExitCode.InvalidConfiguration;
        }
    }

    private async Task<ExitCode> StatusAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadConfigurationPath(args, out string path, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        StatusQuery query = new(configStore, this.Pair());

        StatusOutcome outcome = await query
            .ExecuteAsync(new StatusRequest(path, environment.MachineName), cancellationToken)
            .ConfigureAwait(false);

        // Degradations are never silent: a host whose free space could not be read renders
        // its VMs all the same, and the operator has to know which part is missing.
        foreach (string note in outcome.Notes)
        {
            error.WriteLine($"ripcord: {note}");
        }

        if (outcome.Rendered is { } rendered)
        {
            output.Write(StatusRenderer.Render(
                rendered.View, rendered.Configuration.Peer.OfflineAfter, clock.UtcNow));
            return outcome.Code;
        }

        WriteFailure(error, outcome);
        return outcome.Code;
    }

    /// Read-only, and the one command whose exit code reports on the infrastructure rather
    /// than on the tool: 1 means at least one critical rule is violated (decision D7).
    private async Task<ExitCode> CheckAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadConfigurationPath(args, out string path, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        CheckQuery query = new(configStore, this.Pair(), clock);

        CheckOutcome outcome = await query
            .ExecuteAsync(
                new CheckRequestOptions(path, environment.MachineName), cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Report is { } report)
        {
            output.Write(CheckRenderer.Render(report));
            return outcome.Code;
        }

        WriteFailure(error, new StatusOutcome(
            outcome.Code, null, outcome.Errors, outcome.FailureMessage, []));

        return outcome.Code;
    }

    /// The service entry point. It serves the published snapshot and nothing else — it never
    /// reads Hyper-V, which is the whole point of the privilege split in decision D18.
    private async Task<ExitCode> ServeAsync(
        string[] args, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadOptions(args, out DeployOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        ConfigurationValidation validation = ConfigurationGate.Open(
            configStore,
            options.ConfigurationPath ?? environment.DefaultConfigurationPath,
            environment.MachineName);

        if (validation.Configuration is not { } configuration)
        {
            WriteFailure(error, new StatusOutcome(
                ExitCode.InvalidConfiguration, null, validation.Errors, null, []));
            return ExitCode.InvalidConfiguration;
        }

        // A node with the listener switched off must degrade, not fail: that is the
        // documented off switch.
        if (PeerEndpoint.From(configuration) is not { } endpoint)
        {
            error.WriteLine("ripcord: the listener is disabled on this node, nothing to serve.");
            return ExitCode.Success;
        }

        await peerListener
            .RunAsync(configuration.Listener, endpoint.Rules, cancellationToken)
            .ConfigureAwait(false);

        return ExitCode.Success;
    }

    /// Mutating, so it obeys both rules at once: `--dry-run` shows the plan and stops, and
    /// without it nothing happens until the operator types the node name.
    private ExitCode DeployListener(string[] args, TextWriter output, TextWriter error)
    {
        if (!TryReadOptions(args, out DeployOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            return ExitCode.InvalidConfiguration;
        }

        ListenerDeployment deployment = new(configStore, deploymentExecutor);

        DeploymentOutcome outcome = deployment.Plan(new DeploymentRequest(
            options.ConfigurationPath ?? environment.DefaultConfigurationPath,
            environment.MachineName,
            environment.BinaryPath,
            options.Remove));

        if (outcome.Plan is not { } plan || outcome.Desired is not { } desired)
        {
            WriteDeploymentFailure(error, outcome);
            return outcome.Code;
        }

        output.Write(DeploymentRenderer.Render(plan, desired, options.Remove));

        if (!plan.ChangesAnything)
        {
            return ExitCode.Success;
        }

        if (options.DryRun)
        {
            output.WriteLine("  Nothing was changed. Re-run without --dry-run to apply.");
            return ExitCode.Success;
        }

        if (!this.Confirmed(
            output,
            error,
            "This creates a Windows service and opens an inbound port on this host."))
        {
            return ExitCode.InvalidConfiguration;
        }

        DeploymentResult result = deployment.Apply(plan, desired);
        output.Write(DeploymentRenderer.RenderResult(
            result.Applied, result.Failed, result.FailureMessage));

        return result.Succeeded ? ExitCode.Success : ExitCode.LocalAccessFailure;
    }

    /// Mutating, so it obeys both rules: `--dry-run` shows the whole plan and stops, and
    /// without it nothing is created until the operator types the node name. A test failover
    /// creates and destroys a real VM on this host — a keystroke is not a decision.
    /// The reason the project exists. Everything about it is shaped by being read under
    /// pressure: `--dry-run` prints the whole cross-host plan and stops, and a real run does
    /// nothing at all until the node name is typed in full.
    /// `failover` and `failback` are the same command with the scenario fixed rather than
    /// typed. `failback` is the planned sequence pointed home, so giving it its own parser
    /// would be a second place for the scope rules to drift.
    private async Task<ExitCode> FailoverAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken,
        FailoverOperation? fixedScenario = null)
    {
        if (!TryReadFailoverOptions(
            args, fixedScenario, out FailoverOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            WriteUsage(error);
            return ExitCode.InvalidConfiguration;
        }

        // Rule 3, and the sharpest case of it in the whole tool: this shuts production down and
        // moves it to the other host. A keystroke is not a decision.
        if (!options.DryRun && !this.Confirmed(output, error, Impact(options)))
        {
            return ExitCode.Refused;
        }

        SweepReport sweep = await new FailoverSweepQuery(
                configStore, this.Pair(), provider, audit, clock, FailoverTiming.Default)
            .ExecuteAsync(
                new SweepCommand(
                    options.ConfigurationPath ?? environment.DefaultConfigurationPath,
                    environment.MachineName,
                    options.Scenario,
                    options.Scope,
                    options.DryRun,
                    environment.UserName,
                    this.LocalBuild),
                cancellationToken)
            .ConfigureAwait(false);

        // Printed before the runs, not after: a VM left out of a sweep is something the
        // operator has to know about while they still have the screen in front of them.
        foreach (SweepExclusion excluded in sweep.Excluded)
        {
            error.WriteLine($"ripcord: {excluded.VmName} was left out — {excluded.Reason}");
        }

        foreach (SweptVm swept in sweep.Ran)
        {
            WriteVmOutcome(swept, output, error);
        }

        foreach (string skipped in sweep.NotAttempted)
        {
            error.WriteLine($"ripcord: {skipped} was not attempted; the sweep stopped first");
        }

        if (sweep.Ran.Count == 0)
        {
            WriteFailure(error, new StatusOutcome(
                sweep.Code, null, sweep.Errors, sweep.FailureMessage, []));
        }

        return sweep.Code;
    }

    /// The first command to run on the original primary when it comes back from an unplanned
    /// failover, and before anything else is done to the pair.
    private async Task<ExitCode> FenceAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadFenceOptions(args, out FenceOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            WriteUsage(error);
            return ExitCode.InvalidConfiguration;
        }

        // Rule 3. It changes what this host does on its next boot, which is the whole point,
        // and a host left unable to start its VMs is its own kind of outage.
        if (!options.DryRun && !this.Confirmed(
            output,
            error,
            "This stops the VMs on this host from starting themselves when it reboots."))
        {
            return ExitCode.Refused;
        }

        FenceOutcome outcome = await new FenceQuery(
                configStore, this.Pair(), provider, audit, clock)
            .ExecuteAsync(
                new FenceCommand(
                    options.ConfigurationPath ?? environment.DefaultConfigurationPath,
                    environment.MachineName,
                    options.DryRun,
                    environment.UserName,
                    this.LocalBuild),
                cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Errors.Count > 0)
        {
            WriteFailure(error, new StatusOutcome(
                outcome.Code, null, outcome.Errors, outcome.FailureMessage, []));

            return outcome.Code;
        }

        output.Write(FenceRenderer.Render(outcome, clock.UtcNow));
        return outcome.Code;
    }

    private readonly record struct FenceOptions(string? ConfigurationPath, bool DryRun);

    private static bool TryReadFenceOptions(
        string[] args, out FenceOptions options, out string? error)
    {
        string? path = null;
        bool dryRun = false;

        error = null;
        options = new FenceOptions(null, false);

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;

                case "--config" when !TryConsumeConfig(args, ref index, ref path, out error):
                    return false;

                case "--config":
                    break;

                default:
                    error = $"unexpected argument '{args[index]}'.";
                    return false;
            }
        }

        options = new FenceOptions(path, dryRun);
        return true;
    }

    private static void WriteVmOutcome(SweptVm swept, TextWriter output, TextWriter error)
    {
        FailoverOutcome outcome = swept.Outcome;

        if (outcome.Report is { } report)
        {
            output.Write(FailoverRenderer.Render(report, DateTimeOffset.UtcNow));
            return;
        }

        if (outcome.Code == ExitCode.Refused)
        {
            error.WriteLine(
                $"ripcord: {swept.VmName} refused, nothing was changed: {outcome.FailureMessage}");

            foreach (Finding finding in outcome.Refusal?.Unevaluated ?? [])
            {
                error.WriteLine($"  {finding.Rule.Id}: {finding.Observed}");
            }

            return;
        }

        WriteFailure(error, new StatusOutcome(
            outcome.Code, null, outcome.Errors, outcome.FailureMessage, []));
    }

    /// What the operator is agreeing to, in the words of the scenario they typed. A planned
    /// failover sends the last changes across before it moves; an unplanned one cannot, so
    /// everything written since the last replication cycle is gone — and printing the planned
    /// wording here would conceal the cost of the command being confirmed.
    private static string Impact(FailoverOptions options)
    {
        string subject = options.Scope.NamedVms.Count > 0
            ? $"'{string.Join("', '", options.Scope.NamedVms)}'"
            : options.Scope.Priority is { } tier
                ? $"every {tier} VM"
                : "every VM in this configuration";

        return options.Scenario switch
        {
            FailoverOperation.UnplannedFailover =>
                $"This brings {subject} up on this host from the last replicated point. "
                    + "Everything written since the last replication is lost.",

            FailoverOperation.Failback =>
                $"This shuts down {subject} here and moves it back to the other host.",

            _ => $"This shuts down {subject} and fails it over to the other host.",
        };
    }

    private sealed record FailoverOptions(
        string? ConfigurationPath, SweepScope Scope, FailoverOperation Scenario, bool DryRun);

    /// `--scenario` is required rather than defaulted. Planned and unplanned are different
    /// operations with different consequences, and a default would let the wrong one run
    /// because nobody typed the word.
    ///
    /// So is the scope: exactly one of `--vm`, `--all` and `--priority`. Neither a VM nor a
    /// sweep is a missing argument, not a failover of everything.
    private static bool TryReadFailoverOptions(
        string[] args,
        FailoverOperation? fixedScenario,
        out FailoverOptions options,
        out string? error)
    {
        string? path = null;
        List<string> vmNames = [];
        string? scenario = null;
        string? priority = null;
        bool all = false;
        bool dryRun = false;

        error = null;
        options = new FailoverOptions(
            null, SweepScope.Named([]), FailoverOperation.PlannedFailover, false);

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;

                case "--all":
                    all = true;
                    break;

                case "--vm" when index + 1 < args.Length:
                    vmNames.Add(args[++index]);
                    break;

                case "--vm":
                    error = "--vm needs a VM name.";
                    return false;

                case "--priority" when index + 1 < args.Length:
                    priority = args[++index];
                    break;

                case "--priority":
                    error = "--priority needs a tier.";
                    return false;

                case "--scenario" when index + 1 < args.Length:
                    scenario = args[++index];
                    break;

                case "--scenario":
                    error = "--scenario needs a value.";
                    return false;

                case "--config" when !TryConsumeConfig(args, ref index, ref path, out error):
                    return false;

                case "--config":
                    break;

                default:
                    error = $"unexpected argument '{args[index]}'.";
                    return false;
            }
        }

        FailoverOperation operation;

        if (fixedScenario is { } fixedOperation)
        {
            // `failback` is one operation. A --scenario on it would be the operator naming a
            // second one, and the two could only disagree.
            if (scenario is not null)
            {
                error = "failback takes no --scenario.";
                return false;
            }

            operation = fixedOperation;
        }
        else if (scenario is null)
        {
            error = "--scenario is required: planned or unplanned.";
            return false;
        }
        else if (!TryReadScenario(scenario, out operation))
        {
            error = $"--scenario '{scenario}' is not one this binary runs; "
                + "use planned or unplanned.";
            return false;
        }

        if (!TryReadScope(vmNames, all, priority, out SweepScope scope, out error))
        {
            return false;
        }

        options = new FailoverOptions(path, scope, operation, dryRun);
        return true;
    }

    private static bool TryReadScenario(string written, out FailoverOperation operation)
    {
        operation = FailoverOperation.PlannedFailover;

        switch (written)
        {
            case "planned":
                return true;

            case "unplanned":
                operation = FailoverOperation.UnplannedFailover;
                return true;

            default:
                return false;
        }
    }

    /// Exactly one form. `--all --vm VM-DC-01` has no reading that is obviously right, and a
    /// tool that guesses at one moves production on the guess.
    private static bool TryReadScope(
        List<string> vmNames,
        bool all,
        string? priority,
        out SweepScope scope,
        out string? error)
    {
        scope = SweepScope.Named([]);
        error = null;

        int forms = (vmNames.Count > 0 ? 1 : 0) + (all ? 1 : 0) + (priority is null ? 0 : 1);

        if (forms == 0)
        {
            error = "one of --vm, --all or --priority is required.";
            return false;
        }

        if (forms > 1)
        {
            error = "--vm, --all and --priority are alternatives; use one.";
            return false;
        }

        if (all)
        {
            scope = SweepScope.All;
            return true;
        }

        if (priority is not null)
        {
            // By name, never by ordinal, for the same reason the configuration reads it that
            // way: Enum.TryParse would read "1" as P2 and sweep the second tier.
            if (!Enum.GetNames<VmPriority>().Contains(priority, StringComparer.OrdinalIgnoreCase)
                || !Enum.TryParse(priority, ignoreCase: true, out VmPriority tier))
            {
                error = $"--priority '{priority}' is not a declared tier; "
                    + "use one of " + string.Join(", ", Enum.GetNames<VmPriority>()) + ".";
                return false;
            }

            scope = SweepScope.OfPriority(tier);
            return true;
        }

        scope = SweepScope.Named(vmNames);
        return true;
    }

    private async Task<ExitCode> TestFailoverAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadTestFailoverOptions(args, out TestFailoverOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            WriteUsage(error);
            return ExitCode.InvalidConfiguration;
        }

        // Unattended skips the confirmation and pays for it elsewhere: the run is refused
        // for any VM the configuration has not named, and every unevaluable finding blocks
        // rather than only the six that block an attended run.
        if (!options.DryRun && !options.Unattended && !this.Confirmed(
            output,
            error,
            "This creates a test VM on this host and destroys it again when the test ends."))
        {
            return ExitCode.Refused;
        }

        TestFailoverQuery query = new(
            configStore, this.Pair(), provider, clock, TestFailoverTiming.Default);

        TestFailoverOutcome outcome = await query
            .ExecuteAsync(
                new TestFailoverRequest(
                    options.ConfigurationPath ?? environment.DefaultConfigurationPath,
                    environment.MachineName,
                    options.VmNames,
                    options.All,
                    options.DryRun,
                    options.Unattended),
                cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Report is { } report)
        {
            output.Write(TestFailoverRenderer.Render(
                report, outcome.Configuration?.Replication.TestFailoverSwitch));
            return outcome.Code;
        }

        if (outcome.Code == ExitCode.Refused)
        {
            error.WriteLine($"ripcord: refused, nothing was changed: {outcome.FailureMessage}");
            return outcome.Code;
        }

        WriteFailure(error, new StatusOutcome(
            outcome.Code, null, outcome.Errors, outcome.FailureMessage, []));

        return outcome.Code;
    }

    private readonly record struct TestFailoverOptions(
        string? ConfigurationPath,
        IReadOnlyList<string> VmNames,
        bool All,
        bool DryRun,
        bool Unattended);

    /// `--vm` may be repeated. Neither `--vm` nor `--all` is refused rather than defaulted:
    /// a mutating command with no subject must not guess which VMs were meant.
    private static bool TryReadTestFailoverOptions(
        string[] args, out TestFailoverOptions options, out string? error)
    {
        string? path = null;
        List<string> vmNames = [];
        bool all = false;
        bool dryRun = false;
        bool unattended = false;
        error = null;
        options = default;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;

                case "--all":
                    all = true;
                    break;

                case "--unattended":
                    unattended = true;
                    break;

                case "--vm" when index + 1 < args.Length:
                    vmNames.Add(args[++index]);
                    break;

                case "--vm":
                    error = "--vm needs a VM name.";
                    return false;

                case "--config" when !TryConsumeConfig(args, ref index, ref path, out error):
                    return false;

                case "--config":
                    break;

                default:
                    error = $"unexpected argument '{args[index]}'.";
                    return false;
            }
        }

        if (all && vmNames.Count > 0)
        {
            error = "--all and --vm cannot be combined.";
            return false;
        }

        if (!all && vmNames.Count == 0)
        {
            error = "name the VMs with --vm, or test every one with --all.";
            return false;
        }

        options = new TestFailoverOptions(path, vmNames, all, dryRun, unattended);
        return true;
    }

    private PairReader Pair() =>
        new(
            new LocalStateReader(provider, hostSystemProvider, certificateProvider),
            peerChannel,
            snapshotStore,
            clock,
            this.LocalBuild);

    /// What this binary is, for the snapshot it publishes and for the skew check that reads the
    /// peer's. Both halves, because two builds at the same version from different commits differ
    /// in exactly the way nobody thinks to check.
    internal BuildIdentity LocalBuild =>
        environment.Build ?? new BuildIdentity(BuildInfo.Version, BuildInfo.CommitHash);

    private bool Confirmed(TextWriter output, TextWriter error, string consequence)
    {
        output.WriteLine($"  {consequence}");
        output.Write($"  {ConfirmationPrompt}");

        string? typed = environment.ConfirmationReader?.ReadLine();

        if (string.Equals(typed?.Trim(), environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error.WriteLine("ripcord: not confirmed, nothing was changed.");
        return false;
    }

    private static void WriteDeploymentFailure(TextWriter error, DeploymentOutcome outcome)
    {
        if (outcome.FailureMessage is { } failure)
        {
            error.WriteLine($"ripcord: cannot inspect this host: {failure}");
            return;
        }

        error.WriteLine("ripcord: the configuration cannot be used.");

        foreach (ConfigurationError configurationError in outcome.Errors)
        {
            error.WriteLine($"  {configurationError.Path}: {configurationError.Message}");
        }
    }

    /// Every error at once, so six typos take one run rather than six.
    private static void WriteFailure(TextWriter error, StatusOutcome outcome)
    {
        if (outcome.FailureMessage is { } failure)
        {
            error.WriteLine($"ripcord: cannot read the local Hyper-V state: {failure}");
            return;
        }

        error.WriteLine("ripcord: the configuration cannot be used.");

        foreach (ConfigurationError configurationError in outcome.Errors)
        {
            error.WriteLine(configurationError.Path.Length > 0
                ? $"  {configurationError.Path}: {configurationError.Message}"
                : $"  {configurationError.Message}");
        }
    }

    /// `--config` is parsed identically by every command, and identically wrong would be
    /// three different ways of reading the wrong host's file. Returns false with an error
    /// once the option is malformed or repeated.
    private static bool TryConsumeConfig(
        string[] args, ref int index, ref string? path, out string? error)
    {
        error = null;

        if (path is not null)
        {
            // Taking the last silently would mean reading a file the operator did not mean.
            error = "--config given more than once.";
            return false;
        }

        if (index + 1 >= args.Length)
        {
            error = "--config needs a path.";
            return false;
        }

        path = args[++index];
        return true;
    }

    /// An option given without its value is refused rather than silently falling back to the
    /// default file — reading the wrong host's configuration is the failure this tool exists
    /// to prevent.
    private bool TryReadConfigurationPath(string[] args, out string path, out string? error)
    {
        string? given = null;
        error = null;

        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] != "--config")
            {
                error = $"unexpected argument '{args[index]}'.";
                path = environment.DefaultConfigurationPath;
                return false;
            }

            if (!TryConsumeConfig(args, ref index, ref given, out error))
            {
                path = environment.DefaultConfigurationPath;
                return false;
            }
        }

        path = given ?? environment.DefaultConfigurationPath;
        return true;
    }

    private readonly record struct DeployOptions(
        string? ConfigurationPath, bool DryRun, bool Remove);

    /// Unknown options are refused rather than ignored: on a mutating command, a misspelt
    /// `--dry-run` that silently became a real run is the worst possible outcome.
    private static bool TryReadOptions(
        string[] args, out DeployOptions options, out string? error)
    {
        string? path = null;
        bool dryRun = false;
        bool remove = false;
        error = null;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;

                case "--remove":
                    remove = true;
                    break;

                case "--config" when !TryConsumeConfig(args, ref index, ref path, out error):
                    options = default;
                    return false;

                case "--config":
                    break;

                default:
                    error = $"unexpected argument '{args[index]}'.";
                    options = default;
                    return false;
            }
        }

        options = new DeployOptions(path, dryRun, remove);
        return true;
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("ripcord - disaster recovery for a Hyper-V Replica pair");
        writer.WriteLine();
        writer.WriteLine("  ripcord status [--config <path>]   read both sides of the pair");
        writer.WriteLine("  ripcord check [--config <path>]    would a failover work right now");
        writer.WriteLine("  ripcord deploy-listener [--dry-run] [--remove]");
        writer.WriteLine("                                     install or remove the pair listener");
        writer.WriteLine("  ripcord test-failover (--vm <name> | --all) [--dry-run]");
        writer.WriteLine("                        [--unattended]");
        writer.WriteLine("                                     boot a replica in isolation, then destroy it");
        writer.WriteLine(
            "  ripcord failover --scenario planned|unplanned --vm <name> [--dry-run]");
        writer.WriteLine("                                     move a VM to the other host");
        writer.WriteLine("                                     --all or --priority P1 sweeps");
        writer.WriteLine("  ripcord failback --vm <name> [--dry-run]");
        writer.WriteLine("                                     move a VM back home once the");
        writer.WriteLine("                                     pair is protected again");
        writer.WriteLine("  ripcord fence [--dry-run]          stop this host's VMs starting");
        writer.WriteLine("                                     themselves — run it first when a");
        writer.WriteLine("                                     failed-over host comes back");
        writer.WriteLine("  ripcord serve [--config <path>]    run the read-only pair listener");
        writer.WriteLine("  ripcord version                    version and commit hash");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 success (an unreachable peer included), "
            + "1 a critical rule is violated");
        writer.WriteLine("            or a test failover did not come up, "
            + "2 bad configuration,");
        writer.WriteLine("            3 local access failure, "
            + "4 refused or interrupted - nothing changed,");
        writer.WriteLine("            5 a mutating operation left an intermediate state.");
    }
}
