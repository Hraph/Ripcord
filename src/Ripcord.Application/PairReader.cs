using Ripcord.Domain.Configuration;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Pairing;
using Ripcord.Ports;

namespace Ripcord.Application;

/// Reading `ripcord.yaml` and deciding whether it can be used at all. Shared by every
/// command: three copies of this would be three chances to validate against the wrong
/// machine name.
public static class ConfigurationGate
{
    public static ConfigurationValidation Open(
        IConfigStore configStore, string path, string machineName)
    {
        ArgumentNullException.ThrowIfNull(configStore);

        ConfigurationRead read = configStore.Read(path);

        return read.Errors.Count > 0
            ? ConfigurationValidation.Invalid(read.Errors)
            : ConfigurationValidator.Validate(read.Document, machineName);
    }
}

/// Both sides of the pair, or the reason there is no local side. An unreachable peer is never
/// a failure: it is a state, and it exits Success (decision D7).
public sealed record PairRead(
    PairView? View, string? FailureMessage, IReadOnlyList<string> Notes)
{
    public static PairRead Failed(string? message, IReadOnlyList<string> notes) =>
        new(null, message, notes);
}

/// The local host, published for the peer, then the peer itself — in that order, so the
/// other side's next command sees a fresh snapshot of this one (decision D18).
///
/// Shared by `status` and `check`: they render different things about the same pair, and a
/// second implementation of this sequence would be a second chance to get the order wrong.
public sealed class PairReader(
    LocalStateReader localState,
    IPeerChannel peerChannel,
    ISnapshotStore snapshotStore,
    IClock clock,
    BuildIdentity localBuild)
{
    public async Task<PairRead> ReadAsync(
        RipcordConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        LocalRead local = await localState
            .ReadAsync(configuration, cancellationToken)
            .ConfigureAwait(false);

        if (local.State is not { } state)
        {
            return PairRead.Failed(local.FailureMessage, local.Notes);
        }

        this.Publish(state, configuration);

        PeerFetch fetch = await this
            .FetchAsync(configuration, cancellationToken)
            .ConfigureAwait(false);

        HostState peer = fetch.Snapshot is { } snapshot
            ? snapshot.State with { HostName = configuration.Peer.Hostname }
            : HostState.Unreachable(configuration.Peer.Hostname, fetch.Reachability);

        return new PairRead(
            new PairView(
                state, peer, fetch.Snapshot?.CapturedAt, fetch.Snapshot?.PublishedBy),
            null,
            local.Notes);
    }

    /// Re-reads this host and republishes its snapshot, after something has changed it.
    ///
    /// `status` and `check` publish on their way through, which is enough while every command
    /// is a read. A mutating command is not: once a failover has acted here, the peer is still
    /// holding the snapshot from before, and the peer is the only place the other half of the
    /// sequence can observe what this half did. Republishing is what keeps the read-only
    /// channel sufficient — the alternative being a channel that mutates the peer, which
    /// decision D49 rejected.
    ///
    /// Called whatever the outcome, because a failed run is when the other host most needs an
    /// accurate view of this one.
    public async Task RepublishAsync(
        RipcordConfiguration configuration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        LocalRead local = await localState
            .ReadAsync(configuration, cancellationToken)
            .ConfigureAwait(false);

        if (local.State is { } state)
        {
            this.Publish(state, configuration);
        }
    }

    /// A snapshot this host cannot publish is not a reason to fail the command: both callers
    /// are reads, and the peer simply keeps seeing the previous snapshot until this is fixed.
    /// It is the one write in an otherwise read-only command, and it touches nothing but
    /// Ripcord's own file.
    private void Publish(HostState local, RipcordConfiguration configuration)
    {
        if (!configuration.Listener.Enabled)
        {
            return;
        }

        try
        {
            // The snapshot names the binary that wrote it. The peer needs that before it will
            // move production: a sequence spanning two hosts executed half by each version is
            // the error class that cannot be recovered from at 3 a.m.
            snapshotStore.Write(
                configuration.Listener.SnapshotPath,
                new HostSnapshot(clock.UtcNow, local, localBuild));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Deliberately not fatal; the peer section of the output shows the consequence.
        }
    }

    /// The peer never fails the command, and an unreachable peer is always named from the
    /// configuration: a host that never answered cannot tell us what it is called.
    private async Task<PeerFetch> FetchAsync(
        RipcordConfiguration configuration, CancellationToken cancellationToken)
    {
        if (PeerEndpoint.From(configuration) is not { } endpoint)
        {
            return PeerFetch.Silent(HostReachability.NotConfigured());
        }

        try
        {
            return await peerChannel
                .FetchAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PeerFetch.Silent(HostReachability.Failed(exception.Message, clock.UtcNow));
        }
    }
}
