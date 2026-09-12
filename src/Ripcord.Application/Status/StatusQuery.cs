using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;

namespace Ripcord.Application.Status;

public sealed record StatusRequest(string ConfigurationPath, string MachineName);

/// A successful read: the pair, and the configuration the rendering needs to interpret it.
/// The two only ever exist together, so nothing downstream has to assert that they do.
public sealed record RenderedStatus(PairView View, RipcordConfiguration Configuration);

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
public sealed class StatusQuery(IConfigStore configStore, PairReader pairReader)
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

        return new StatusOutcome(
            ExitCode.Success,
            new RenderedStatus(view, configuration),
            [],
            null,
            read.Notes);
    }
}
