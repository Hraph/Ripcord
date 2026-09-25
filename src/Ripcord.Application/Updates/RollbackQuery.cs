using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Failover;
using Ripcord.Domain.Updates;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Updates;
using Ripcord.Ports;

namespace Ripcord.Application.Updates;

public sealed record RollbackRequest(
    string ConfigurationPath, string MachineName, string BinaryPath, BuildIdentity LocalBuild);

/// What the exchange did, and the sentence for it. The exit code is the part read afterwards:
/// 3 says the host is where it was, 5 says somebody has to look.
public sealed record RollbackApplied(ExitCode Code, string? Message);

public sealed record RollbackOutcome(
    ExitCode Code,
    RollbackPlan? Plan,
    string RunningVersion,
    IReadOnlyList<ConfigurationError> Errors,
    string? FailureMessage,
    IReadOnlyList<string> Notes);

/// What going back would do, and then doing it.
///
/// Unlike `update`, nothing here is gated on `updates.check` or `updates.install`. Those two
/// switches exist because reaching for a release means reaching off the host, and this reaches
/// for a file that is already on it. A host forbidden to fetch anything is exactly the host
/// most likely to need this.
public sealed class RollbackQuery(
    IConfigStore configStore, IBinarySwap swap, PairReader pairReader, IClock clock)
{
    public async Task<RollbackOutcome> ExecuteAsync(
        RollbackRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ConfigurationValidation validation = ConfigurationGate.Open(
            configStore, request.ConfigurationPath, request.MachineName);

        if (validation.Configuration is not { } configuration)
        {
            return new RollbackOutcome(
                ExitCode.InvalidConfiguration,
                null,
                request.LocalBuild.Version,
                validation.Errors,
                null,
                []);
        }

        StagedBinaries staged;

        try
        {
            staged = swap.Observe(request.BinaryPath);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new RollbackOutcome(
                ExitCode.LocalAccessFailure,
                null,
                request.LocalBuild.Version,
                [],
                $"what is beside the binary could not be read: {exception.Message}",
                []);
        }

        (VersionSkew skew, OperatingMode mode, List<string> notes) =
            await this.ConsequencesAsync(configuration, request.LocalBuild, cancellationToken)
                .ConfigureAwait(false);

        RollbackPlan plan = RollbackPlan.For(new RollbackSubject(
            staged.HasInterruptedSwap,
            staged.HasPreviousBinary,
            staged.PreviousVersion,
            staged.PreviousSetAsideAt,
            request.LocalBuild.Version,
            skew,
            mode,
            configuration.Peer.Hostname,
            staged.PreviousInUse));

        return new RollbackOutcome(
            plan.Halt is null ? ExitCode.Success : ExitCode.InvalidConfiguration,
            plan,
            request.LocalBuild.Version,
            [],
            null,
            notes);
    }

    /// The exchange itself, once the operator has typed the node name.
    ///
    /// The exit code follows what the swap says it managed, and the distinction is the whole
    /// point. There **is** a window in which this host has no binary where the service starts
    /// from — short, but real — so "it failed and the host is where it was" and "it failed
    /// part way and could not be put back" cannot share an answer. The first is worth
    /// re-running; the second is somebody driving to the site.
    public RollbackApplied Apply(string binaryPath)
    {
        try
        {
            return swap.SwapWithPrevious(binaryPath) switch
            {
                SwapOutcome.Exchanged => new RollbackApplied(ExitCode.Success, null),

                SwapOutcome.Recovered => new RollbackApplied(
                    ExitCode.LocalAccessFailure,
                    "the exchange could not be completed and the running binary was put back; "
                    + "this host runs what it was running"),

                SwapOutcome.LeftIncomplete => new RollbackApplied(
                    ExitCode.IntermediateState,
                    "the exchange could not be completed and the running binary could not be "
                    + "put back; this host may have no binary where the service starts from, "
                    + "and the one it was running is beside it under '.swap'"),

                _ => new RollbackApplied(
                    ExitCode.LocalAccessFailure,
                    "nothing was moved; this host runs what it was running"),
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Thrown before the running binary was touched: the copy that precedes every move
            // is what fails for want of room or permission, and it changes nothing.
            return new RollbackApplied(
                ExitCode.LocalAccessFailure,
                $"{exception.Message}; nothing was moved and this host runs what it was running");
        }
    }

    /// Best effort, exactly as the update's is: going back needs neither Hyper-V nor the peer,
    /// and refusing because the local host cannot be read would refuse the retreat somebody is
    /// making *because* the host cannot be read.
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
                "the pair could not be read, so what going back does to a failover could not "
                + "be checked; it is listed below as if the pair agreed");

            return (VersionSkew.Between(localBuild, null), OperatingMode.Normal, notes);
        }

        return (
            VersionSkew.Between(localBuild, view.PeerBuild),
            CheckSubject.From(view, configuration, clock.UtcNow).Mode,
            notes);
    }
}
