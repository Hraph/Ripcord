namespace Ripcord.Domain.Updates;

public enum UpdateVerdict
{
    UpToDate,

    /// A newer release exists. That is the whole claim: nothing downloads and nothing
    /// installs, so the worst this can be is a wrong version number.
    UpdateAvailable,

    /// One of the two could not be read. Its own answer, never folded into UpToDate —
    /// "cannot be shown to be current" and "is current" are different claims.
    NotComparable,
}

public sealed record UpdateStatus(UpdateVerdict Verdict, string Explanation)
{
    public static UpdateStatus Between(string? running, string? latest)
    {
        if (!TryRead(running, out Version? current) || !TryRead(latest, out Version? published))
        {
            return new UpdateStatus(
                UpdateVerdict.NotComparable,
                $"this build reports '{Shown(running)}' and the latest release "
                + $"'{Shown(latest)}'; a version that cannot be read cannot be compared");
        }

        if (published > current)
        {
            return new UpdateStatus(
                UpdateVerdict.UpdateAvailable,
                $"this host runs {current} and {published} has been released; "
                + "download and install are manual, and the signature is verified by hand");
        }

        return published < current
            ? new UpdateStatus(
                UpdateVerdict.UpToDate,
                $"this host runs {current}, which is ahead of the latest release {published}")
            : new UpdateStatus(UpdateVerdict.UpToDate, $"this host runs {current}, the latest release");
    }

    /// A tag is written `v0.5.0` as often as `0.5.0`, and the running build carries its commit
    /// as `0.4.0+abc123def456`. Neither part is a version difference.
    private static bool TryRead(string? text, out Version? version)
    {
        string trimmed = (text ?? "").Trim();

        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        int build = trimmed.IndexOf('+', StringComparison.Ordinal);

        if (build >= 0)
        {
            trimmed = trimmed[..build];
        }

        return Version.TryParse(trimmed, out version);
    }

    private static string Shown(string? text) => string.IsNullOrWhiteSpace(text) ? "nothing" : text;
}
