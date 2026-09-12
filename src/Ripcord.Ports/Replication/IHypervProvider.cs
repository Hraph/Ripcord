using Ripcord.Domain.Replication;

namespace Ripcord.Ports.Replication;

/// The local host's Hyper-V, read-only. Mutating operations arrive with the milestone that
/// needs them, not before.
///
/// The peer is not here: since decision D18 it is read from a published snapshot over
/// IPeerChannel, not from Hyper-V. An exception from this port means a local failure — WMI
/// down, privileges missing, timeout on our own host — which is its own exit code.
public interface IHypervProvider
{
    Task<HostState> GetLocalStateAsync(CancellationToken cancellationToken);
}
