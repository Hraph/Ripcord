namespace Ripcord.Domain.Replication;

/// What `ripcord status` renders. Both sides are always present as values; either may be
/// unreachable, which is a state, not an absence.
public sealed record PairView(HostState Local, HostState Peer);
