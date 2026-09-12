using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Domain.Pairing;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Pairing;
using Ripcord.Ports;

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
public sealed class StatusQuery(
    IConfigStore configStore,
    LocalStateReader localState,
    IPeerChannel peerChannel,
    ISnapshotStore snapshotStore,
    IClock clock)
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

        LocalRead localRead = await localState
            .ReadAsync(configuration, cancellationToken)
            .ConfigureAwait(false);

        if (localRead.State is not { } local)
        {
            return new StatusOutcome(
                ExitCode.LocalAccessFailure,
                null,
                [],
                localRead.FailureMessage,
                localRead.Notes);
        }

        // Published before the peer is read, so the peer's own `status` sees a fresh snapshot
        // of this host. It is the one write in an otherwise read-only command, and it touches
        // nothing but Ripcord's own file.
        this.Publish(local, configuration);

        PeerFetch fetch = await this.FetchPeerAsync(configuration, cancellationToken)
            .ConfigureAwait(false);

        HostState peer = fetch.Snapshot is { } snapshot
            ? snapshot.State with { HostName = configuration.Peer.Hostname }
            : HostState.Unreachable(configuration.Peer.Hostname, fetch.Reachability);

        return new StatusOutcome(
            ExitCode.Success,
            new RenderedStatus(
                new PairView(local, peer, fetch.Snapshot?.CapturedAt), configuration),
            [],
            null,
            localRead.Notes);
    }

    /// A snapshot this host cannot publish is not a reason to fail the command: `status` is a
    /// read, and the peer simply keeps seeing the previous snapshot until this is fixed.
    private void Publish(HostState local, RipcordConfiguration configuration)
    {
        if (!configuration.Listener.Enabled)
        {
            return;
        }

        try
        {
            snapshotStore.Write(
                configuration.Listener.SnapshotPath, new HostSnapshot(clock.UtcNow, local));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Deliberately silent here; the peer section shows the consequence.
        }
    }

    /// The peer never fails the command, and an unreachable peer is always named from the
    /// configuration: a host that never answered cannot tell us what it is called.
    private async Task<PeerFetch> FetchPeerAsync(
        RipcordConfiguration configuration, CancellationToken cancellationToken)
    {
        if (PeerEndpoint.From(configuration) is not { } endpoint)
        {
            return PeerFetch.Silent(HostReachability.NotConfigured());
        }

        try
        {
            return await peerChannel.FetchAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PeerFetch.Silent(
                HostReachability.Failed(exception.Message, clock.UtcNow));
        }
    }

    private static StatusOutcome Invalid(IReadOnlyList<ConfigurationError> errors) =>
        new(ExitCode.InvalidConfiguration, null, errors, null, []);
}
