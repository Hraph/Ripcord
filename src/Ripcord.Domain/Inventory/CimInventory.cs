using System.Globalization;
using System.Text.RegularExpressions;

namespace Ripcord.Domain.Inventory;

/// The string and number handling the WMI inventory would otherwise have to do. It is all
/// pure, and the adapter cannot be run off Windows, so it lives here where a test can reach
/// it — the same reasoning as `CimTranslation`.
public static partial class CimInventory
{
    /// `Msvm_EthernetPortAllocationSettingData.HostResource[0]` is a WMI object path, not a
    /// name. The switch's friendly name is on the switch instance, so the identifier has to
    /// come out of the path first.
    ///
    /// `Name=` is matched only after a `.` or a `,`, because `CreationClassName=` ends in the
    /// same three letters and comes first in the path.
    public static string? SwitchIdOf(string? hostResource) =>
        hostResource is not null && SwitchName().Match(hostResource) is { Success: true } match
            ? match.Groups[1].Value
            : null;

    /// `Msvm_ReplicationSettingData.IncludedDisks` is an array of embedded instances rendered
    /// as MOF text. Each one carries the disk it covers in its own `HostResource`.
    ///
    /// Parsing MOF text is not elegant. The alternative is for the adapter to decide what
    /// counts as an included disk, and that decision could then not be tested until someone
    /// was standing at a Windows host.
    public static IReadOnlyList<string> IncludedDiskPaths(IEnumerable<string?>? embedded)
    {
        if (embedded is null)
        {
            return [];
        }

        List<string> paths = [];

        foreach (string? instance in embedded)
        {
            if (instance is null)
            {
                continue;
            }

            foreach (Match match in HostResource().Matches(instance))
            {
                paths.Add(Unescape(match.Groups[1].Value));
            }
        }

        return paths;
    }

    /// MOF text escapes a backslash as two, so every Windows path comes back doubled.
    public static string Unescape(string value) =>
        value.Replace(@"\\", @"\", StringComparison.Ordinal);

    /// `Msvm_MemorySettingData` reports megabytes already — `VirtualQuantity` is startup RAM,
    /// `Limit` the dynamic maximum, `Reservation` the dynamic minimum. A value past int is a
    /// provider fault rather than a machine, and reads as unknown.
    public static int? Megabytes(ulong? value) =>
        value is { } megabytes && megabytes <= int.MaxValue ? (int)megabytes : null;

    /// `Msvm_EthernetSwitchPortVlanSettingData.OperationMode`: 1 access, 2 trunk, 3 private.
    /// Only an access port has a single VLAN to compare; anything else reads as untagged,
    /// which still compares equal across a correctly matched pair.
    public static int? AccessVlan(ushort? operationMode, ushort? accessVlanId) =>
        operationMode == AccessMode && accessVlanId is { } vlanId ? vlanId : null;

    /// A dynamic MAC is the absence of a static one. Null stays null: an unread flag is not a
    /// static address, and the rule that asks has to be able to say it could not tell.
    public static bool? UsesDynamicMac(bool? staticMacAddress) =>
        staticMacAddress is { } isStatic ? !isStatic : null;

    /// `Msvm_SyntheticEthernetPortSettingData.Address` is twelve hexadecimal characters with
    /// no separators. Anything else — empty, absent, a placeholder — is not an address.
    public static string? MacAddressOf(string? address) => MacAddress.Normalise(address);

    /// Hyper-V's own disk subtypes, matched on the invariant part of the string. These are
    /// resource subtypes rather than display names, so they are not localized.
    public static bool IsVirtualHardDisk(string? resourceSubType) =>
        Contains(resourceSubType, "Virtual Hard Disk");

    public static bool IsPhysicalDisk(string? resourceSubType) =>
        Contains(resourceSubType, "Physical Disk Drive");

    private const ushort AccessMode = 1;

    private static bool Contains(string? value, string fragment) =>
        value is not null && value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[.,]Name=""([^""]+)""", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex SwitchName();

    [GeneratedRegex(
        @"HostResource\s*=\s*\{\s*""([^""]+)""",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 500)]
    private static partial Regex HostResource();
}
