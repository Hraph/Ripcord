using Ripcord.Domain.Replication;

namespace Ripcord.Domain.Pairing;

/// What one attempt to read the peer produced: a snapshot, or the reason there is none.
/// Never both, and never an exception — an unreachable peer is a state, not a failure.
public sealed record PeerFetch(HostSnapshot? Snapshot, HostReachability Reachability)
{
    public static PeerFetch Answered(HostSnapshot snapshot) =>
        new(snapshot, HostReachability.Reachable());

    public static PeerFetch Silent(HostReachability reachability) => new(null, reachability);
}
