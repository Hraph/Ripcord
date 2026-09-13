using Ripcord.Application.Checks;
using Ripcord.Application.Deployment;
using Ripcord.Application.Status;
using Ripcord.Application.TestFailover;
using Ripcord.Application;
using Ripcord.Cli.Rendering;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Pairing;
using Ripcord.Ports.Deployment;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Hosts;
using Ripcord.Ports.Pairing;
using Ripcord.Ports.Replication;
using Ripcord.Ports;

namespace Ripcord.Cli;

/// What the composition root knows about the machine. Passed in rather than read here so the
/// whole command surface is exercisable from the Linux test container.
public sealed record CliEnvironment(
    string MachineName,
    string DefaultConfigurationPath,
    string BinaryPath,
    TextReader? ConfirmationReader = null);

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
    private async Task<ExitCode> TestFailoverAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (!TryReadTestFailoverOptions(args, out TestFailoverOptions options, out string? optionError))
        {
            error.WriteLine($"ripcord: {optionError}");
            WriteUsage(error);
            return ExitCode.InvalidConfiguration;
        }

        if (!options.DryRun && !this.Confirmed(
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
                    options.DryRun),
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
        string? ConfigurationPath, IReadOnlyList<string> VmNames, bool All, bool DryRun);

    /// `--vm` may be repeated. Neither `--vm` nor `--all` is refused rather than defaulted:
    /// a mutating command with no subject must not guess which VMs were meant.
    private static bool TryReadTestFailoverOptions(
        string[] args, out TestFailoverOptions options, out string? error)
    {
        string? path = null;
        List<string> vmNames = [];
        bool all = false;
        bool dryRun = false;
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

        options = new TestFailoverOptions(path, vmNames, all, dryRun);
        return true;
    }

    private PairReader Pair() =>
        new(
            new LocalStateReader(provider, hostSystemProvider, certificateProvider),
            peerChannel,
            snapshotStore,
            clock);

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
        writer.WriteLine("                                     boot a replica in isolation, then destroy it");
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
