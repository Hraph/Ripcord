namespace Ripcord.Domain.Replication;

/// Why a host could not be read, and since when. A refused connection means a listener that
/// is not running; a timeout means a host that may be dead. They call for different actions,
/// so they never collapse into one "unreachable".
public enum ReachabilityKind
{
    Reachable,
    NotConfigured,
    TimedOut,
    Refused,
    Failed,
}

public sealed record HostReachability(
    ReachabilityKind Kind,
    string Reason,
    DateTimeOffset? UnreachableSince)
{
    public static HostReachability Reachable() =>
        new(ReachabilityKind.Reachable, "reachable", null);

    public static HostReachability NotConfigured() =>
        new(ReachabilityKind.NotConfigured, "no peer channel configured on this node", null);

    public static HostReachability TimedOut(DateTimeOffset since) =>
        new(ReachabilityKind.TimedOut, "no answer before the timeout", since);

    public static HostReachability Refused(DateTimeOffset since) =>
        new(ReachabilityKind.Refused, "connection refused", since);

    public static HostReachability Failed(string reason, DateTimeOffset since) =>
        new(ReachabilityKind.Failed, reason, since);

    public bool IsReachable => Kind == ReachabilityKind.Reachable;

    public TimeSpan? UnreachableFor(DateTimeOffset now) =>
        IsReachable ? null : Elapsed.Between(UnreachableSince, now);

    /// A peer never contacted at all counts as offline: nothing has ever been heard from it,
    /// which is the state milestone 1 always reports.
    public PeerPresence Presence(TimeSpan offlineAfter, DateTimeOffset now)
    {
        if (IsReachable)
        {
            return PeerPresence.Reachable;
        }

        return UnreachableFor(now) is { } elapsed && elapsed < offlineAfter
            ? PeerPresence.Silent
            : PeerPresence.Offline;
    }
}
