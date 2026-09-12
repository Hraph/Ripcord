using Ripcord.Domain.Pairing;

namespace Ripcord.Ports.Pairing;

/// The read-only channel to the other host. Deliberately not part of IHypervProvider: since
/// decision D18 the peer view does not come from Hyper-V at all, it comes from a snapshot the
/// peer published. Modelling it as a Hyper-V read would misdescribe both the privilege and
/// the freshness.
///
/// Never throws for an unreachable peer — that is a PeerFetch carrying the reason.
public interface IPeerChannel
{
    Task<PeerFetch> FetchAsync(PeerEndpoint endpoint, CancellationToken cancellationToken);
}
