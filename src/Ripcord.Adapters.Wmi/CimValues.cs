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

    public static ushort? Number(CimInstance? instance, string propertyName) =>
        Value(instance, propertyName) is ushort number ? number : null;

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
