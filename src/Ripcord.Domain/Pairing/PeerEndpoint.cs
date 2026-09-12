using Ripcord.Domain.Configuration;

namespace Ripcord.Domain.Pairing;

/// Everything needed to reach the peer and decide whether the thing that answered really is
/// the peer. Assembled from configuration here rather than in the transport, so the mapping
/// is testable without a socket.
public sealed record PeerEndpoint(
    string Address,
    int Port,
    PeerRules Rules,
    string LocalCertificateThumbprint,
    TimeSpan Timeout)
{
    /// Null when this node has no listener configured — the documented off switch. The client
    /// side is governed by the same flag as the server side: a node that publishes nothing
    /// does not go looking either.
    public static PeerEndpoint? From(RipcordConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        ListenerSettings listener = configuration.Listener;

        if (!listener.Enabled
            || listener.PeerCertificateThumbprint is not { } peerThumbprint
            || listener.LocalCertificateThumbprint is not { } localThumbprint)
        {
            return null;
        }

        return new PeerEndpoint(
            configuration.Peer.Address,
            listener.Port,
            new PeerRules(
                peerThumbprint,
                $"CN={configuration.Peer.Hostname}",
                configuration.Peer.Address),
            localThumbprint,

            // Not `offline_after_sec`: that is how long silence lasts before the peer is
            // called offline, a much longer thing than how long one attempt may hang.
            TimeSpan.FromSeconds(10));
    }
}
