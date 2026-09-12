namespace Ripcord.Domain.Replication;

/// Silence short of `peer.offline_after_sec` is not yet an outage. Milestone 4 turns Offline
/// into a decision, so the distinction is drawn here rather than in the rendering.
public enum PeerPresence
{
    Reachable,
    Silent,
    Offline,
}
