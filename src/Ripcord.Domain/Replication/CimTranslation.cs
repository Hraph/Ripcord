namespace Ripcord.Domain.Replication;

/// Everything the WMI adapter would otherwise have to decide. The adapter cannot be run off
/// Windows, so each of these lives here where a test can reach it.
public static class CimTranslation
{
    /// Msvm_ComputerSystem holds both the host and its VMs. InstallDate is documented as the
    /// moment the VM configuration was created, and Null for the management operating system.
    ///
    /// Caption would be the obvious filter and is the wrong one: it is localized, it carries
    /// the AMENDMENT qualifier, and on a non-English Windows it matches nothing — `status`
    /// would then report a host with no VM at all, a confident wrong answer rather than a
    /// failure. This project has already been bitten by localized firewall rule names.
    public static bool IsVirtualMachine(DateTime? installDate) => installDate is not null;

    /// Extended replication gives a VM several relationships, told apart by the InstanceID
    /// suffix: `\HVR\0` is the primary one, `\HVR\1` the extended. Milestone 1 renders the
    /// primary. The suffix did not exist before Windows Server 2012 R2, so an InstanceID
    /// without one is accepted rather than discarded.
    public static bool IsPrimaryRelationship(string? instanceId) =>
        instanceId is null
        || !instanceId.Contains(@"\HVR\", StringComparison.OrdinalIgnoreCase)
        || instanceId.EndsWith(@"\HVR\0", StringComparison.OrdinalIgnoreCase);

    public static string VmName(string? elementName) =>
        string.IsNullOrWhiteSpace(elementName) ? "(unnamed)" : elementName;

    /// MI hands back CIM_DATETIME as a DateTime whose kind it sets. Unspecified is read as the
    /// host's own wall clock, since the value came from that host's Hyper-V. Assumed, not
    /// verified — see V22.
    public static DateTimeOffset? Instant(DateTime? value) => value is { } instant
        ? new DateTimeOffset(instant.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(instant, DateTimeKind.Local)
            : instant)
        : null;
}
