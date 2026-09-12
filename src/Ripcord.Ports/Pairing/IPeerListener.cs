using Ripcord.Domain.Configuration;
using Ripcord.Domain.Pairing;

namespace Ripcord.Ports.Pairing;

/// Serves this host's published snapshot to the peer, and nothing else. Runs until cancelled.
public interface IPeerListener
{
    Task RunAsync(
        ListenerSettings settings, PeerRules rules, CancellationToken cancellationToken);
}
