namespace Ripcord.Domain.Configuration;


/// A configuration that has been validated. Nothing here is nullable except what the schema
/// declares optional, so no consumer re-checks what the validator already settled.
public sealed record RipcordConfiguration(
    NodeSettings Node,
    PeerSettings Peer,
    ListenerSettings Listener,
    ReplicationSettings Replication,
    StorageSettings Storage,
    IReadOnlyList<VmSettings> Vms,
    IReadOnlyList<Acknowledgement> Acknowledgements);

public sealed record NodeSettings(string Hostname, int HostMemoryReserveGb);

/// Which side this host normally is. Without it neither "which host is the failover target"
/// nor "the direction is inverted" is answerable: the two configuration files are mirror
/// images and nothing observable says which way round the pair is meant to be.
///
/// The operating mode is still derived from observed state (decision D20) — this is only the
/// baseline the observation is compared against.
public enum ExpectedRole
{
    Primary,
    Replica,
}

/// What the pair is expected to look like. Every field here is compared against something
/// observed; nothing in it is acted upon.
public sealed record ReplicationSettings(
    ExpectedRole ExpectedRole,
    string ExpectedSwitchName,
    TimeSpan ExpectedFrequency,
    int LagWarningMultiplier,
    TimeSpan HealthWarningAfter,
    string? TestFailoverSwitch = null)
{
    /// Hyper-V health flickers to Warning for a single missed cycle. Five minutes is long
    /// enough that a blip does not wake anyone and short enough to catch a real stall.
    public static readonly TimeSpan DefaultHealthWarningAfter = TimeSpan.FromMinutes(5);
}

public sealed record StorageSettings(
    string DataVolume, int FreeSpaceWarningGb, bool CheckBitLockerAutoUnlock);

/// A finding the operator has seen, accepted and dated. The expiry is mandatory (decision
/// D20): without it an acknowledgement is a rule deleted by the back door, and the permanent
/// critical it was written for outlives the reason it was accepted.
public sealed record Acknowledgement(
    string RuleId, string? VmName, string Reason, DateTimeOffset Expires)
{
    public bool IsActiveAt(DateTimeOffset now) => now < this.Expires;

    /// A host-scoped acknowledgement — no VM named — covers the host-level findings of its
    /// rule, and never spreads to a per-VM one: silencing one VM's missing switch must not
    /// silence the other two.
    public bool Covers(string ruleId, string? vmName) =>
        this.RuleId == ruleId
        && string.Equals(this.VmName, vmName, StringComparison.OrdinalIgnoreCase);
}

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
    int? ExpectedStartupRamMb,
    DateTimeOffset? GuestOsSupportEnds = null);
