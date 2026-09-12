using Ripcord.Application.Status;
using Ripcord.Application;
using Ripcord.Cli.Rendering;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Replication;
using Ripcord.Ports;

namespace Ripcord.Cli;

/// What the composition root knows about the machine. Passed in rather than read here so the
/// whole command surface is exercisable from the Linux test container.
public sealed record CliEnvironment(string MachineName, string DefaultConfigurationPath);

/// Argument parsing and console rendering. No decision lives here: the exit code comes from
/// the use case, the layout from StatusRenderer.
public sealed class RipcordCli(
    IConfigStore configStore, IHypervProvider provider, IClock clock, CliEnvironment environment)
{
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

        StatusQuery query = new(configStore, provider, clock);

        StatusOutcome outcome = await query
            .ExecuteAsync(new StatusRequest(path, environment.MachineName), cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Rendered is { } rendered)
        {
            output.Write(StatusRenderer.Render(
                rendered.View, rendered.Configuration.Peer.OfflineAfter, clock.UtcNow));
            return outcome.Code;
        }

        WriteFailure(error, outcome);
        return outcome.Code;
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

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("ripcord — disaster recovery for a Hyper-V Replica pair");
        writer.WriteLine();
        writer.WriteLine("  ripcord status [--config <path>]   read both sides of the pair");
        writer.WriteLine("  ripcord version                    version and commit hash");
        writer.WriteLine();
        writer.WriteLine("Exit codes: 0 success (an unreachable peer included), "
            + "2 bad configuration, 3 local access failure.");
    }
}
