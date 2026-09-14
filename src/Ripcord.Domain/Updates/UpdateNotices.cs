using System.Globalization;

namespace Ripcord.Domain.Updates;

/// A release this host heard about, and when it heard. Written by whatever last looked —
/// nothing on these hosts looks on its own, and nothing looks during a command.
public sealed record UpdateNotice(string Version, DateTimeOffset SeenAt);

/// Whether a stored notice is still worth putting in front of somebody, and what to say.
///
/// The file is a record of what was true when it was written. This is the decision about
/// whether it is true now — after an update has been installed it still names the release
/// that was new then, and repeating it would send an operator to run an update that has
/// already happened.
///
/// It appears above a verdict that matters more than it does, on a console being read during
/// an incident, so it says nothing at all unless it is both true and useful. Anything that
/// cannot be established — an unreadable version on either side — is silence rather than a
/// guess.
public static class UpdateNotices
{
    public static string? For(UpdateNotice? notice, string? runningVersion, DateTimeOffset now)
    {
        if (notice is null)
        {
            return null;
        }

        UpdateStatus status = UpdateStatus.Between(runningVersion, notice.Version);

        if (status.Verdict != UpdateVerdict.UpdateAvailable)
        {
            return null;
        }

        // The date it was heard, not how long ago: a clock that was wrong, or a file copied
        // from the other host, would otherwise be reported as an age in the future.
        return $"{notice.Version} is available. Run `ripcord update`. "
            + $"Seen {notice.SeenAt.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.";
    }
}
