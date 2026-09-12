namespace Ripcord.Domain.Configuration;

/// A configuration that has been validated. Nothing here is nullable except what the schema
/// declares optional, so no consumer re-checks what the validator already settled.
public sealed record RipcordConfiguration(
    NodeSettings Node,
    PeerSettings Peer,
    IReadOnlyList<VmSettings> Vms);

public sealed record NodeSettings(string Hostname);

public sealed record PeerSettings(string Hostname, string Address, TimeSpan OfflineAfter);

/// Failover order. The declared set is the whole set: a priority outside it is a typo, and a
/// typo that silently became a third tier would reorder a failover.
public enum VmPriority
{
    P1,
    P2,
}

public sealed record VmSettings(
    string Name,
    VmPriority Priority,
    bool IsDomainController,
    bool HasPassthroughDisk,
    int? ExpectedStartupRamMb);
