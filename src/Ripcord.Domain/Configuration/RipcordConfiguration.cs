using Ripcord.Domain.Alerting;
using Ripcord.Domain.Dashboard;
using Ripcord.Domain.Updates;

namespace Ripcord.Domain.Configuration;


/// A configuration that has been validated. Nothing here is nullable except what the schema
/// declares optional, so no consumer re-checks what the validator already settled.
public sealed record RipcordConfiguration(
    NodeSettings Node,
    PeerSettings Peer,
    ListenerSettings Listener,
    ReplicationSettings Replication,
    StorageSettings Storage,
    AlertingSettings Alerting,
    UpdateSettings Updates,
    DashboardSettings Dashboard,
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
    string? TestFailoverSwitch = null,
    TimeSpan? TestFailoverOrphanAfterOverride = null,
    IReadOnlyList<string>? UnattendedTestFailoverVmsOrNone = null)
{
    /// Hyper-V health flickers to Warning for a single missed cycle. Five minutes is long
    /// enough that a blip does not wake anyone and short enough to catch a real stall.
    public static readonly TimeSpan DefaultHealthWarningAfter = TimeSpan.FromMinutes(5);

    /// Long enough that a slow test failover is never called an orphan, short enough that a
    /// run interrupted overnight is reported the next morning rather than a week later.
    public static readonly TimeSpan DefaultOrphanAfter = TimeSpan.FromHours(24);

    public TimeSpan TestFailoverOrphanAfter =>
        this.TestFailoverOrphanAfterOverride ?? DefaultOrphanAfter;

    /// Unattended running is an authorisation, so the empty list is the default and means no
    /// VM may be tested without a human typing the node name.
    public IReadOnlyList<string> UnattendedTestFailoverVms =>
        this.UnattendedTestFailoverVmsOrNone ?? [];

    public bool AuthorisedUnattended(string vmName) =>
        this.UnattendedTestFailoverVms.Contains(vmName, StringComparer.OrdinalIgnoreCase);
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

    /// The shipped samples' two values. Well-formed, so without this they pass as real and the
    /// failure surfaces as a SILENT peer, which reads as a network fault.
    public static readonly string[] SamplePlaceholders =
    [
        "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555",
        "1111AAAA2222BBBB3333CCCC4444DDDD5555EEEE",
    ];

    /// Beside the configuration file, which is beside the binary — with the log, the audit
    /// trail and the alert state. A default on another volume is a default that does not
    /// exist on a host that has no such volume, and the listener would be deployed onto a
    /// path Windows cannot even grant access to.
    public const string DefaultSnapshotFileName = "state.json";

    /// What the file is, in the words the operator reads it in.
    public const string SnapshotMeaning =
        "the snapshot 'ripcord status' writes and the listener serves to the peer";

    /// The default up to 0.4: a configuration that still names it was most likely copied
    /// from an older sample, on a host that may have no D: volume at all.
    public const string LegacyDefaultSnapshotPath = @"D:\Ripcord\state.json";

    public static bool IsLegacyDefault(string? path) =>
        string.Equals(
            path?.Trim().Replace('/', '\\'),
            LegacyDefaultSnapshotPath,
            StringComparison.OrdinalIgnoreCase);

    public static ListenerSettings Disabled() =>
        new(false, DefaultPort, null, null, DefaultSnapshotFileName);
}

/// Failover order. The declared set is the whole set: a priority outside it is a typo, and a
/// typo that silently became a third tier would reorder a failover.
public enum VmPriority
{
    P1,
    P2,
}

/// Whether a sweep may pick this VM up (decision D19). Not a statement about how well the VM
/// would run on the other host: `VM-BACKUP-01` is `manual` because it boots without its 4 TB
/// repository and can fill the target volume the VMs that matter are already living on.
///
/// `never` is the stronger form — a VM that must not be failed over by this tool at all, even
/// when somebody names it. Nothing in the current configuration uses it; it is here because
/// "excluded from sweeps" and "not to be moved" are different statements, and a `manual` VM
/// answering for both would make the weaker one unsayable.
public enum FailoverPolicy
{
    Auto,
    Manual,
    Never,
}

public sealed record VmSettings(
    string Name,
    VmPriority Priority,
    bool IsDomainController,
    bool HasPassthroughDisk,
    int? ExpectedStartupRamMb,
    DateTimeOffset? GuestOsSupportEnds = null,
    FailoverPolicy Failover = FailoverPolicy.Auto);
