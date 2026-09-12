namespace Ripcord.Domain.Configuration;

/// `ripcord.yaml` exactly as it was read, before anything has been checked. Every field is
/// nullable because every field may be absent from the file — that absence is what the
/// validator reports. Sections this milestone does not use are deliberately not modelled;
/// they are ignored, not validated, until the milestone that reads them.
public sealed class ConfigurationDocument
{
    public int? SchemaVersion { get; set; }

    public NodeDocument? Node { get; set; }

    public PeerDocument? Peer { get; set; }

    public ReplicationDocument? Replication { get; set; }

    public ListenerDocument? Listener { get; set; }

    public StorageDocument? Storage { get; set; }

    public List<VmDocument>? Vms { get; set; }

    public ChecksDocument? Checks { get; set; }
}

public sealed class NodeDocument
{
    public string? Hostname { get; set; }

    public int? HostMemoryReserveGb { get; set; }
}

/// The replication expectations `ripcord check` compares reality against. The port, the auth
/// mode and the certificate subjects are in the shipped sample and belong to the Hyper-V
/// configuration rather than to any rule, so they stay unread (decision D23).
public sealed class ReplicationDocument
{
    public string? ExpectedRole { get; set; }

    public string? ExpectedSwitchName { get; set; }

    public string? TestFailoverSwitch { get; set; }

    public int? TestFailoverOrphanAfterHours { get; set; }

    public int? ExpectedFrequencySec { get; set; }

    public int? LagWarningMultiplier { get; set; }

    public int? HealthWarningAfterSec { get; set; }
}

public sealed class StorageDocument
{
    public string? DataVolume { get; set; }

    public int? FreeSpaceWarningGb { get; set; }

    public bool CheckBitlockerAutounlock { get; set; }
}

/// Absent on a host that has acknowledged nothing, which is the normal case.
public sealed class ChecksDocument
{
    public List<AcknowledgementDocument>? Acknowledgements { get; set; }
}

/// A date with no time and no offset in YAML; read as midnight UTC so the two hosts agree on
/// when an acknowledgement lapses whatever their local time zone.
public sealed class AcknowledgementDocument
{
    public string? Rule { get; set; }

    public string? Vm { get; set; }

    public string? Reason { get; set; }

    public DateTime? Expires { get; set; }
}

public sealed class PeerDocument
{
    public string? Hostname { get; set; }

    public string? Address { get; set; }

    public int? OfflineAfterSec { get; set; }
}

/// Absent entirely on a node with no listener, which is the documented off switch.
public sealed class ListenerDocument
{
    public bool Enabled { get; set; }

    public int? Port { get; set; }

    public string? LocalCertificateThumbprint { get; set; }

    public string? PeerCertificateThumbprint { get; set; }

    public string? SnapshotPath { get; set; }
}

public sealed class VmDocument
{
    public string? Name { get; set; }

    public string? Priority { get; set; }

    public bool IsDomainController { get; set; }

    public bool HasPassthroughDisk { get; set; }

    public int? ExpectedStartupRamMb { get; set; }

    public DateTime? GuestOsSupportEnds { get; set; }
}
