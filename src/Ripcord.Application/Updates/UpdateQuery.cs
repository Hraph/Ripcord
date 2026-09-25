using Ripcord.Domain.Configuration;
using Ripcord.Domain.Updates;
using Ripcord.Domain;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Updates;

namespace Ripcord.Application.Updates;

public sealed record UpdateCheckOptions(
    string ConfigurationPath, string MachineName, string RunningVersion);

/// Either an answer or the reason there is none. A lookup that failed is never reported as a
/// host that is up to date.
public sealed record UpdateOutcome(
    ExitCode Code,
    UpdateStatus? Status,
    IReadOnlyList<ConfigurationError> Errors,
    string? FailureMessage,
    /// The tag the feed named, whether or not it is newer than this build. Carried so the
    /// answer can be written down: it is what every later command reads instead of looking.
    string? Version = null);

/// Asks whether a newer release exists, when the operator has said this host may ask. It
/// reports and stops there: nothing downloads, nothing installs, and the exposure stays
/// bounded to a wrong version number.
public sealed class UpdateQuery(IConfigStore configStore, IReleaseFeed feed)
{
    public async Task<UpdateOutcome> ExecuteAsync(
        UpdateCheckOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        ConfigurationValidation validation = ConfigurationGate.Open(
            configStore, options.ConfigurationPath, options.MachineName);

        if (validation.Configuration is not { } configuration)
        {
            return new UpdateOutcome(
                ExitCode.InvalidConfiguration, null, validation.Errors, null);
        }

        // Refused rather than quietly skipped: a host that was asked to check and silently did
        // not is a host somebody believes is checking.
        if (!configuration.Updates.Check)
        {
            return new UpdateOutcome(
                ExitCode.InvalidConfiguration,
                null,
                [],
                UpdateSettings.CheckOff);
        }

        ReleaseLookup lookup = await feed.LatestAsync(cancellationToken).ConfigureAwait(false);

        if (lookup.Version is null)
        {
            return new UpdateOutcome(
                ExitCode.LocalAccessFailure, null, [], lookup.FailureMessage);
        }

        UpdateStatus status = UpdateStatus.Between(options.RunningVersion, lookup.Version);

        // "Could not be compared" is a failure to answer, and it exits as one: a scheduled
        // check must not read a non-answer as good news.
        return new UpdateOutcome(
            status.Verdict == UpdateVerdict.NotComparable
                ? ExitCode.LocalAccessFailure
                : ExitCode.Success,
            status,
            [],
            null,
            lookup.Version);
    }
}
