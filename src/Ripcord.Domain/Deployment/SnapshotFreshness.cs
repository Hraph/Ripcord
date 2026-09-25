namespace Ripcord.Domain.Deployment;

public enum SnapshotAge
{
    Fresh,

    /// No file: the listener serves nothing, and the peer shows this host offline.
    Missing,

    /// Older than the peer's offline threshold: the peer shows this host stale.
    Stale,
}

/// Whether the file the listener serves is one the peer will believe. Only `status`, `check`,
/// `failover` and `fence` write it, so a listener can run for days serving an old one.
public static class SnapshotFreshness
{
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
