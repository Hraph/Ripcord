namespace Ripcord.Domain.Configuration;

/// A configuration that has been validated. Nothing here is nullable except what the schema
/// declares optional, so no consumer re-checks what the validator already settled.
public sealed record RipcordConfiguration(
    NodeSettings Node,
    PeerSettings Peer,
    ListenerSettings Listener,
    IReadOnlyList<VmSettings> Vms);

public sealed record NodeSettings(string Hostname);

public sealed record PeerSettings(string Hostname, string Address, TimeSpan OfflineAfter);

/// The pair channel. Disabled is a first-class state, not a missing configuration: a node with
/// the listener off degrades to the local-only view rather than failing.
public sealed record ListenerSettings(
    bool Enabled,
    int Port,
    string? LocalCertificateThumbprint,
    string? PeerCertificateThumbprint,
    string SnapshotPath)
{
    public const int DefaultPort = 7443;

    public const string DefaultSnapshotPath = @"D:\Ripcord\state.json";

    public static ListenerSettings Disabled() =>
        new(false, DefaultPort, null, null, DefaultSnapshotPath);
}

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
