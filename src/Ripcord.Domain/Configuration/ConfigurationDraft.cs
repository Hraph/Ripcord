using System.Globalization;

namespace Ripcord.Domain.Configuration;

/// Everything the interview collected, before it is a file.
///
/// Only the sections `ripcord init` owns. The listener, alerting and the dashboard are absent
/// from this type on purpose: each of them means "off" when the key is missing, and a host being
/// set up has nothing to say about any of them yet. Whether it may look for a newer release is
/// the exception — whether the host has outbound access is known on the day it is set up.
///
/// `Carried` is the rest of those sections — the fields inside `replication`, `storage` and
/// each VM that the interview never asks about. They are here rather than left out because a
/// re-run rewrites the whole section, and a section rewritten from six answers is a section
/// that has quietly dropped `failover: manual` and `has_passthrough_disk: true` from a VM
/// somebody marked that way on purpose.
public sealed record ConfigurationDraft(
    string NodeHostname,
    int HostMemoryReserveGb,
    string PeerHostname,
    string PeerAddress,
    int PeerOfflineAfterSec,
    ExpectedRole Role,
    string SwitchName,
    int FrequencySec,
    int LagMultiplier,
    string DataVolume,
    int FreeSpaceWarningGb,
    bool CheckUpdates,
    IReadOnlyList<DraftVm> Vms,
    CarriedSettings Carried)
{
    public bool Equals(ConfigurationDraft? other) =>
        other is not null
        && this.NodeHostname == other.NodeHostname
        && this.HostMemoryReserveGb == other.HostMemoryReserveGb
        && this.PeerHostname == other.PeerHostname
        && this.PeerAddress == other.PeerAddress
        && this.PeerOfflineAfterSec == other.PeerOfflineAfterSec
        && this.Role == other.Role
        && this.SwitchName == other.SwitchName
        && this.FrequencySec == other.FrequencySec
        && this.LagMultiplier == other.LagMultiplier
        && this.DataVolume == other.DataVolume
        && this.FreeSpaceWarningGb == other.FreeSpaceWarningGb
        && this.CheckUpdates == other.CheckUpdates
        && this.Carried == other.Carried
        && Structural.Same(this.Vms, other.Vms);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(this.NodeHostname);
        hash.Add(this.PeerHostname);
        hash.Add(this.Role);
        Structural.Add(ref hash, this.Vms);
        return hash.ToHashCode();
    }
}

/// One VM. The first three are asked; the rest are what the file already said about it, kept
/// so that re-running the interview is not a way to lose them.
public sealed record DraftVm(
    string Name,
    VmPriority Priority,
    bool IsDomainController,
    bool HasPassthroughDisk = false,
    int? ExpectedStartupRamMb = null,
    DateTime? GuestOsSupportEnds = null,
    string? Failover = null);

/// The fields of the owned sections that no question covers. None of them is ever asked for and
/// none is ever invented: absent on a first run, and whatever the file said on every run after.
public sealed record CarriedSettings(
    int? HealthWarningAfterSec = null,
    string? TestFailoverSwitch = null,
    int? TestFailoverOrphanAfterHours = null,
    IReadOnlyList<string>? UnattendedTestFailoverVms = null,
    bool? CheckBitlockerAutounlock = DraftDefaults.CheckBitlockerAutounlock,
    bool InstallUpdates = false)
{
    public static readonly CarriedSettings None = new();

    public bool Equals(CarriedSettings? other) =>
        other is not null
        && this.HealthWarningAfterSec == other.HealthWarningAfterSec
        && this.TestFailoverSwitch == other.TestFailoverSwitch
        && this.TestFailoverOrphanAfterHours == other.TestFailoverOrphanAfterHours
        && this.CheckBitlockerAutounlock == other.CheckBitlockerAutounlock
        && this.InstallUpdates == other.InstallUpdates
        && Structural.Same(this.UnattendedTestFailoverVms, other.UnattendedTestFailoverVms);

    public override int GetHashCode() =>
        HashCode.Combine(
            this.HealthWarningAfterSec,
            this.TestFailoverSwitch,
            this.TestFailoverOrphanAfterHours,
            this.CheckBitlockerAutounlock);
}

/// The defaults the interview offers when the file has nothing to say. Figures rather than
/// opinions: a 4 GB reserve on a host sized for these VMs, two minutes before a silent peer is
/// called offline, and the replication frequency Hyper-V itself defaults to.
public static class DraftDefaults
{
    public const int HostMemoryReserveGb = 4;

    public const int PeerOfflineAfterSec = 120;

    public const int FrequencySec = 30;

    public const int LagMultiplier = 3;

    public const string DataVolume = "D:";

    public const int FreeSpaceWarningGb = 200;

    /// On, as the shipped samples have it. A check switched off by default is a check nobody
    /// notices is not running.
    public const bool CheckBitlockerAutounlock = true;

    public static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
