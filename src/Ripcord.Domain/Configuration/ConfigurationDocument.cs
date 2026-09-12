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

    public List<VmDocument>? Vms { get; set; }
}

public sealed class NodeDocument
{
    public string? Hostname { get; set; }
}

public sealed class PeerDocument
{
    public string? Hostname { get; set; }

    public string? Address { get; set; }

    public int? OfflineAfterSec { get; set; }
}

public sealed class VmDocument
{
    public string? Name { get; set; }

    public string? Priority { get; set; }

    public bool IsDomainController { get; set; }

    public bool HasPassthroughDisk { get; set; }

    public int? ExpectedStartupRamMb { get; set; }
}
