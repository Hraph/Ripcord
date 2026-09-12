using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports;

namespace Ripcord.Application;

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
    string? FailureMessage)
{
    public PairView? View => this.Rendered?.View;

    public RipcordConfiguration? Configuration => this.Rendered?.Configuration;
}

/// Reads both sides of the pair. The configuration is validated first, so a run on the wrong
/// host stops before it has read anything from Hyper-V.
public sealed class StatusQuery(IConfigStore configStore, IHypervProvider provider, IClock clock)
{
    public async Task<StatusOutcome> ExecuteAsync(
        StatusRequest request, CancellationToken cancellationToken)
    {
        ConfigurationRead read = configStore.Read(request.ConfigurationPath);

        if (read.Errors.Count > 0)
        {
            return Invalid(read.Errors);
        }

        ConfigurationValidation validation =
            ConfigurationValidator.Validate(read.Document, request.MachineName);

        if (validation.Configuration is not { } configuration)
        {
            return Invalid(validation.Errors);
        }

        HostState local;

        try
        {
            local = await provider.GetLocalStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new StatusOutcome(
                ExitCode.LocalAccessFailure, null, [], exception.Message);
        }

        HostState peer = await this.ReadPeerAsync(configuration, cancellationToken)
            .ConfigureAwait(false);

        return new StatusOutcome(
            ExitCode.Success,
            new RenderedStatus(new PairView(local, peer), configuration),
            [],
            null);
    }

    /// The peer never fails the command. An unreachable peer is always named from the
    /// configuration: a host that never answered cannot tell us what it is called, and the
    /// provider has no business guessing.
    private async Task<HostState> ReadPeerAsync(
        RipcordConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            HostState peer = await provider.GetPeerStateAsync(cancellationToken)
                .ConfigureAwait(false);

            return peer.IsReachable
                ? peer
                : peer with { HostName = configuration.Peer.Hostname };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HostState.Unreachable(
                configuration.Peer.Hostname,
                HostReachability.Failed(exception.Message, clock.UtcNow));
        }
    }

    private static StatusOutcome Invalid(IReadOnlyList<ConfigurationError> errors) =>
        new(ExitCode.InvalidConfiguration, null, errors, null);
}
