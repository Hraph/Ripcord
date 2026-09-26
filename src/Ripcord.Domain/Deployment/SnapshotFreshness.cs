namespace Ripcord.Domain.Deployment;

public enum SnapshotAge
{
    Fresh,

    /// No file: the listener serves nothing, and the peer shows this host offline.
    Missing,

    /// Older than the peer's offline threshold: the peer shows this host stale.
    Stale,
}

/// Why the peer is served no current snapshot, and the one command that changes that.
public sealed record SnapshotRemedy(string Why, string Next);

/// Whether the file the listener serves is one the peer will believe. The publishing service
/// rewrites it every few seconds; `status`, `check`, `failover` and `fence` write it too.
public static class SnapshotFreshness
{
    /// Null when the snapshot is current. Otherwise the cause is read from the publisher, the
    /// one thing meant to keep it current: absent, not running, or running and failing.
    public static SnapshotRemedy? Remedy(SnapshotAge? age, ObservedService? publisher)
    {
        if (age is not (SnapshotAge.Missing or SnapshotAge.Stale))
        {
            return null;
        }

        return publisher switch
        {
            null or { Installed: false } => new(
                "the publishing service is not installed", "ripcord service install"),
            { State: ServiceRunState.Unknown } => new(
                "the publishing service's state could not be read", "ripcord service"),
            { State: ServiceRunState.Running or ServiceRunState.StartPending } => new(
                "the publishing service runs and has not written it: its log says why, and "
                    + "so does publishing once by hand",
                "ripcord publish"),
            _ => new("the publishing service is not running", "ripcord service start"),
        };
    }

    /// With no threshold, only a missing file is reported.
    public static SnapshotAge Judge(
        DateTimeOffset? writtenAt, DateTimeOffset now, TimeSpan? staleAfter)
    {
        if (writtenAt is not { } written)
        {
            return SnapshotAge.Missing;
        }

        return staleAfter is { } threshold && now - written > threshold
            ? SnapshotAge.Stale
            : SnapshotAge.Fresh;
    }
}
