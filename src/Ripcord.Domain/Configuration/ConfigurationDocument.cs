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

    /// Absent on a host that notifies nobody, which is the default: these two machines are
    /// meant to have no outbound access at all.
    public AlertingDocument? Alerting { get; set; }

    public UpdatesDocument? Updates { get; set; }

    /// Absent on a host that serves no page, which is the default.
    public DashboardDocument? Dashboard { get; set; }

    /// Read even when the rest of the file is refused: the log is where the refusal is
    /// explained, so it cannot wait for the file to be valid.
    public DiagnosticsDocument? Diagnostics { get; set; }
}

/// No address key: the page is served on the loopback interface by construction, so
/// there is nothing here an operator can widen by accident.
public sealed class DashboardDocument
{
    public bool Enabled { get; set; }

    public int? Port { get; set; }

    public int? RefreshSec { get; set; }
}

/// Absent on every host that has no outbound access, which is both of them by design.
public sealed class UpdatesDocument
{
    public bool Check { get; set; }

    /// Whether this host may replace its own binary. Separate from `Check` because looking is
    /// not installing, and the two are worth being able to say apart.
    public bool Install { get; set; }
}

public sealed class AlertingDocument
{
    public bool Enabled { get; set; }

    public int? RepeatAfterHours { get; set; }

    public string? QuietHours { get; set; }

    public SmtpDocument? Smtp { get; set; }

    public WebhookDocument? Webhook { get; set; }
}

/// `Password` is modelled only so it can be refused by name. The deserialiser ignores keys it
/// does not know, and a relay password silently ignored is a relay password sitting in every
/// backup of the configuration directory (decision D24).
public sealed class SmtpDocument
{
    public string? Host { get; set; }

    public int? Port { get; set; }

    public bool StartTls { get; set; }

    public string? From { get; set; }

    public List<string>? To { get; set; }

    public string? Username { get; set; }

    public string? PasswordSecret { get; set; }

    public string? Password { get; set; }
}

public sealed class WebhookDocument
{
    public string? Url { get; set; }
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

    public List<string>? UnattendedTestFailoverVms { get; set; }

    public int? ExpectedFrequencySec { get; set; }

    public int? LagWarningMultiplier { get; set; }

    public int? HealthWarningAfterSec { get; set; }
}

public sealed class StorageDocument
{
    public string? DataVolume { get; set; }

    public int? FreeSpaceWarningGb { get; set; }

    /// Nullable so that absence survives a rewrite. `ripcord init` puts back what it found,
    /// and a plain bool would turn "the key was never there" into "the key says false" — the
    /// same behaviour, written down as a decision nobody made.
    public bool? CheckBitlockerAutounlock { get; set; }
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

    /// Absent on every VM that fails over like the rest, which is most of them.
    public string? Failover { get; set; }
}

/// Absent on a host nobody has had to debug yet, which is every host until the day it is not.
/// The whole block is optional and so is every key in it; what it cannot do is stop a command.
public sealed class DiagnosticsDocument
{
    /// Nullable on purpose: the log is on unless the file says otherwise, so "the key is
    /// absent" and "the key says false" have to be different answers.
    public bool? Enabled { get; set; }

    public string? Path { get; set; }

    public int? MaxSizeMb { get; set; }
}
