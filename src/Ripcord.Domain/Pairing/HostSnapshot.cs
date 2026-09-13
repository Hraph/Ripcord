using Ripcord.Domain.Replication;

namespace Ripcord.Domain.Pairing;

/// A host's state as it was at one instant, plus that instant. The listener never talks to
/// Hyper-V (decision D18), so the peer view is always a snapshot someone wrote earlier — its
/// age is part of the answer, not a detail.
/// `PublishedBy` is the Ripcord that wrote this snapshot, not a property of the host. The
/// failover sequences are encoded in the binary and span two hosts, so the peer's build is
/// something the local side has to be able to read before it moves production.
public sealed record HostSnapshot(
    DateTimeOffset CapturedAt, HostState State, BuildIdentity? PublishedBy = null)
{
    public TimeSpan AgeAt(DateTimeOffset now) => Elapsed.Between(CapturedAt, now) ?? TimeSpan.Zero;

    /// The threshold is the one the operator already configured: `peer.offline_after_sec`,
    /// the same duration that decides when silence becomes an outage.
    public bool IsFreshAt(DateTimeOffset now, TimeSpan threshold) => AgeAt(now) < threshold;
}
