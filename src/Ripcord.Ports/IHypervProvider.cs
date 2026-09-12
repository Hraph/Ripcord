using Ripcord.Domain.Replication;

namespace Ripcord.Ports;

/// Read-only at this milestone. Mutating operations arrive with the milestone that needs
/// them, not before.
///
/// Neither method throws for an unreachable host: that is a HostState with a reachability
/// other than Reachable. Exceptions are reserved for a local failure — WMI down, privileges
/// missing, timeout on our own host — which is a different exit code.
public interface IHypervProvider
{
    Task<HostState> GetLocalStateAsync(CancellationToken cancellationToken);

    Task<HostState> GetPeerStateAsync(CancellationToken cancellationToken);
}
