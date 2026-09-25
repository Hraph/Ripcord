using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Failover;
using Ripcord.Domain.Replication;
using Ripcord.Domain.Updates;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Updates;
using Ripcord.Ports;

namespace Ripcord.Application.Updates;

public sealed record UpdatePlanRequest(
    string ConfigurationPath,
    string MachineName,
    BuildIdentity LocalBuild,

    /// Where this binary is, to look at what an earlier update left beside it.
    string? BinaryPath = null);

/// The plan, and everything needed to print it. `Version` is the tag the release was published
/// under, carried because the download addresses it by name and nothing else knows it.
public sealed record UpdatePlanOutcome(
    ExitCode Code,
    UpdatePlan? Plan,
    RipcordConfiguration? Configuration,
    string? Version,
    IReadOnlyList<ConfigurationError> Errors,
    string? FailureMessage,
    IReadOnlyList<string> Notes);

/// Works out what updating this host would do, and changes nothing. The pair is read for the
/// consequences only — which build the other side runs, and whether production is currently
/// living here — so a host whose Hyper-V cannot be reached still gets a plan, with the
/// consequences it could not compute said out loud rather than omitted.
public sealed class UpdatePlanQuery(
    IConfigStore configStore,
    IReleaseFeed feed,
    PairReader pairReader,
    IClock clock,
    IBinarySwap? swap = null)
{
    public async Task<UpdatePlanOutcome> ExecuteAsync(
        UpdatePlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ConfigurationValidation validation = ConfigurationGate.Open(
            configStore, request.ConfigurationPath, request.MachineName);

        if (validation.Configuration is not { } configuration)
        {
            return Failed(ExitCode.InvalidConfiguration, validation.Errors, null);
        }

        // Refused rather than quietly skipped, exactly as `check-update` refuses: a host that
        // was asked to update and silently did not is a host somebody believes is current.
        if (!configuration.Updates.Check)
        {
            return Failed(
                ExitCode.InvalidConfiguration,
                [],
                "update checking is switched off in the configuration (updates.check); "
                + "these hosts are meant to have no outbound access.");
        }

        ReleaseLookup lookup = await feed.LatestAsync(cancellationToken).ConfigureAwait(false);

        if (lookup.Version is null)
        {
            return Failed(ExitCode.LocalAccessFailure, [], lookup.FailureMessage);
        }

        UpdateStatus status = UpdateStatus.Between(request.LocalBuild.Version, lookup.Version);

        (VersionSkew skew, OperatingMode mode, List<string> notes) =
            await this.ConsequencesAsync(configuration, request.LocalBuild, cancellationToken)
                .ConfigureAwait(false);

        UpdatePlan plan = UpdatePlan.For(new UpdateSubject(
            status,
            configuration.Updates.Install,
            skew,
            mode,
            configuration.Peer.Hostname,
            this.PreviousInUse(request.BinaryPath)));

        return new UpdatePlanOutcome(
            Code(plan, status), plan, configuration, lookup.Version, [], null, notes);
    }

    /// Best effort by design. Updating a binary needs neither Hyper-V nor the peer, and
    /// refusing because the local host could not be read would refuse the update somebody is
    /// running *because* the host cannot be read.
    private async Task<(VersionSkew, OperatingMode, List<string>)> ConsequencesAsync(
        RipcordConfiguration configuration,
        BuildIdentity localBuild,
        CancellationToken cancellationToken)
    {
        PairRead read = await pairReader
            .ReadAsync(configuration, cancellationToken)
            .ConfigureAwait(false);

        List<string> notes = [.. read.Notes];

        if (read.View is not { } view)
        {
            notes.Add(
                "the pair could not be read, so what this update does to a failover could "
                + "not be checked; it is listed below as if the pair agreed");

            return (
                VersionSkew.Between(localBuild, null), OperatingMode.Normal, notes);
        }

        return (
            VersionSkew.Between(localBuild, view.PeerBuild),
            CheckSubject.From(view, configuration, clock.UtcNow).Mode,
            notes);
    }

    /// Unread is not in use: the discard step then reports what stopped it.
    private bool PreviousInUse(string? binaryPath)
    {
        if (swap is null || binaryPath is null)
        {
            return false;
        }

        try
        {
            return swap.Observe(binaryPath).PreviousInUse;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
            return false;
        }
    }

    /// A halt is not one exit code: "this host may not install" is a configuration answer and
    /// "the versions cannot be compared" is a failure to read one.
    private static ExitCode Code(UpdatePlan plan, UpdateStatus status) =>
        plan.Halt is null
            ? ExitCode.Success
            : status.Verdict == UpdateVerdict.NotComparable
                ? ExitCode.LocalAccessFailure
                : ExitCode.InvalidConfiguration;

    private static UpdatePlanOutcome Failed(
        ExitCode code, IReadOnlyList<ConfigurationError> errors, string? message) =>
        new(code, null, null, null, errors, message, []);
}
