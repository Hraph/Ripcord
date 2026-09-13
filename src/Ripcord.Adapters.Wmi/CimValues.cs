using Microsoft.Management.Infrastructure;

namespace Ripcord.Adapters.Wmi;

/// Property reads, shared by the two WMI providers. Every one of them tolerates an absent
/// property: the exact set differs between Windows Server builds, and a missing property must
/// degrade to "unknown" rather than throw halfway through an inventory.
internal static class CimValues
{
    public static object? Value(CimInstance? instance, string propertyName) =>
        instance?.CimInstanceProperties[propertyName]?.Value;

    public static string? Text(CimInstance? instance, string propertyName) =>
        Value(instance, propertyName) as string;

    /// Widened past `ushort` deliberately. The MOF declares some of the properties read here
    /// as `uint32` — `Msvm_EthernetSwitchPortVlanSettingData.OperationMode` and
    /// `Win32_EncryptableVolume.ProtectionStatus` among them — and a `is ushort` pattern
    /// would never match those, silently reporting "not read" for a value that was read
    /// perfectly well. Out of range still degrades to unknown rather than wrapping.
    public static ushort? Number(CimInstance? instance, string propertyName) =>
        Value(instance, propertyName) switch
        {
            ushort number => number,
            uint number and <= ushort.MaxValue => (ushort)number,
            ulong number and <= ushort.MaxValue => (ushort)number,
            short number and >= 0 => (ushort)number,
            int number and >= 0 and <= ushort.MaxValue => (ushort)number,
            _ => null,
        };

    public static DateTime? Instant(CimInstance? instance, string propertyName) =>
        Value(instance, propertyName) is DateTime value ? value : null;

    public static bool? Flag(CimInstance? instance, string propertyName) =>
        Value(instance, propertyName) is bool flag ? flag : null;

    /// CIM sizes arrive as uint64 more often than not, and a value past long.MaxValue is a
    /// provider bug rather than a disk — it degrades to unknown instead of going negative.
    public static long? Size(CimInstance? instance, string propertyName) =>
        Value(instance, propertyName) switch
        {
            ulong size and <= long.MaxValue => (long)size,
            uint size => size,
            long size and >= 0 => size,
            _ => null,
        };
}
