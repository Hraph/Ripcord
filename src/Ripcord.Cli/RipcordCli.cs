using Ripcord.Application.Deployment;
using Ripcord.Application.Status;
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

        StatusQuery query = new(
            configStore, this.LocalState(), peerChannel, snapshotStore, clock);

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

        ConfigurationRead read = configStore.Read(
            options.ConfigurationPath ?? environment.DefaultConfigurationPath);

        ConfigurationValidation validation = read.Errors.Count > 0
            ? ConfigurationValidation.Invalid(read.Errors)
            : ConfigurationValidator.Validate(read.Document, environment.MachineName);

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

        if (!this.Confirmed(output, error))
        {
            return ExitCode.InvalidConfiguration;
        }

        DeploymentResult result = deployment.Apply(plan, desired);
        output.Write(DeploymentRenderer.RenderResult(
            result.Applied, result.Failed, result.FailureMessage));

        return result.Succeeded ? ExitCode.Success : ExitCode.LocalAccessFailure;
    }

    private LocalStateReader LocalState() =>
        new(provider, hostSystemProvider, certificateProvider);

    private bool Confirmed(TextWriter output, TextWriter error)
    {
        output.WriteLine(
            "  This creates a Windows service and opens an inbound port on this host.");
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

    /// An option given without its value is refused rather than silently falling back to the
    /// default file — reading the wrong host's configuration is the failure this tool exists
    /// to prevent.
    private bool TryReadConfigurationPath(string[] args, out string path, out string? error)
    {
        path = environment.DefaultConfigurationPath;
        error = null;

        bool given = false;

        for (int index = 0; index < args.Length; index++)
        {
            if (args[index] != "--config")
            {
                error = $"unexpected argument '{args[index]}'.";
                return false;
            }

            if (index + 1 >= args.Length)
            {
                error = "--config needs a path.";
                return false;
            }

            // Taking the last silently would mean reading a file the operator did not mean.
            if (given)
            {
                error = "--config given more than once.";
                return false;
            }

            path = args[index + 1];
            given = true;
            index++;
        }

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

                case "--config" when index + 1 < args.Length && path is null:
                    path = args[++index];
                    break;

                case "--config" when path is not null:
                    error = "--config given more than once.";
                    options = default;
                    return false;

                case "--config":
                    error = "--config needs a path.";
                    options = default;
                    return false;

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
        writer.WriteLine("ripcord — disaster recovery for a Hyper-V Replica pair");
        writer.WriteLine();
        writer.WriteLine("  ripcord status [--config <path>]   read both sides of the pair");
        writer.WriteLine("  ripcord deploy-listener [--dry-run] [--remove]");
        writer.WriteLine("                                     install or remove the pair listener");
        writer.WriteLine("  ripcord serve [--config <path>]    run the read-only pair listener");
        writer.WriteLine("  ripcord version                    version and commit hash");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 success (an unreachable peer included), "
            + "2 bad configuration, 3 local access failure.");
    }
}
