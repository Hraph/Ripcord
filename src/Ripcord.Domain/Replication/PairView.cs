namespace Ripcord.Domain.Replication;

/// What `ripcord status` renders. Both sides are always present as values; either may be
/// unreachable, which is a state, not an absence.
///
/// PeerCapturedAt is when the peer's state was true, not when it was fetched: since decision
/// D18 the peer publishes a snapshot rather than answering live, and the age of that snapshot
/// is part of the answer. Null when the peer said nothing.
public sealed record PairView(HostState Local, HostState Peer, DateTimeOffset? PeerCapturedAt)
{
    public PairView(HostState local, HostState peer)
        : this(local, peer, null)
    {
    }
}
