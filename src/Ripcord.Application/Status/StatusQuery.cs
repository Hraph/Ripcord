using Ripcord.Application.Deployment;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Deployment;
using Ripcord.Ports.Diagnostics;
using Ripcord.Domain.Diagnostics;

namespace Ripcord.Application.Status;

/// `ThisBuild` and `ThisBinary` are the running command's, to tell whether the listener runs
/// the build on disk.
public sealed record StatusRequest(
    string ConfigurationPath, string MachineName, string ThisBuild, string ThisBinary);

/// A successful read: the pair, and the configuration the rendering needs to interpret it.
/// The two only ever exist together, so nothing downstream has to assert that they do.
/// `Listener` is null when the other host can read this one as far as this side can tell;
/// `Running` then says so, unless the listener is switched off.
public sealed record RenderedStatus(
    PairView View,
    RipcordConfiguration Configuration,
    ListenerAlert? Listener = null,
    ListenerRunning? Running = null);

/// Everything `ripcord status` produced: the exit code, and whichever of the pair, the
/// configuration errors or the failure message explains it.
public sealed record StatusOutcome(
    ExitCode Code,
    RenderedStatus? Rendered,
    IReadOnlyList<ConfigurationError> Errors,
    string? FailureMessage,
    IReadOnlyList<string> Notes)
{
    public PairView? View => this.Rendered?.View;

    public RipcordConfiguration? Configuration => this.Rendered?.Configuration;
}

/// Reads both sides of the pair. The configuration is validated first, so a run on the wrong
/// host stops before it has read anything from Hyper-V.
public sealed class StatusQuery(
    IConfigStore configStore,
    PairReader pairReader,
    IDeploymentExecutor executor,
    IDiagnosticLogReader logReader)
{
    public async Task<StatusOutcome> ExecuteAsync(
        StatusRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ConfigurationValidation validation = ConfigurationGate.Open(
            configStore, request.ConfigurationPath, request.MachineName);

        if (validation.Configuration is not { } configuration)
        {
            return new StatusOutcome(
                ExitCode.InvalidConfiguration, null, validation.Errors, null, []);
        }

        PairRead read = await pairReader
            .ReadAsync(configuration, cancellationToken)
            .ConfigureAwait(false);

        if (read.View is not { } view)
        {
            return new StatusOutcome(
                ExitCode.LocalAccessFailure, null, [], read.FailureMessage, read.Notes);
        }

        ObservedService service = ServiceReading.Read(executor);

        return new StatusOutcome(
            ExitCode.Success,
            new RenderedStatus(
                view,
                configuration,
                ListenerAvailability.Judge(
                    configuration.Listener, configuration.Peer.Hostname, service),
                ListenerAvailability.Running(
                    configuration.Listener,
                    service,
                    ServiceProcessReading.Judge(
                        logReader,
                        service,
                        LogFolder.Beside(service.BinaryPath ?? request.ThisBinary),
                        request.ThisBuild,
                        request.ThisBinary))),
            [],
            null,
            [.. ConfigurationNotes.Of(configuration), .. read.Notes]);
    }
}
