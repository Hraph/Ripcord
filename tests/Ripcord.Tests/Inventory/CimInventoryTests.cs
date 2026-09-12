using Ripcord.Domain.Inventory;

namespace Ripcord.Tests.Inventory;

/// The string handling the WMI inventory needs. It is here rather than in the adapter for one
/// reason: `WmiHypervProvider` cannot be run off Windows, so anything it decided could not be
/// tested until someone was standing at a Windows host.
public class CimInventoryTests
{
    /// A real `HostResource` from `Msvm_EthernetPortAllocationSettingData`. `Name=` has to be
    /// matched after the `,` — `CreationClassName=` ends in the same three letters and comes
    /// first in the path.
    [Fact]
    public void The_switch_identifier_comes_out_of_the_host_resource_path()
    {
        string hostResource =
            @"\\HV-PRIMARY-01\root\virtualization\v2:Msvm_VirtualEthernetSwitch."
            + @"CreationClassName=""Msvm_VirtualEthernetSwitch"","
            + @"Name=""C08CB7B8-9B3C-4EFF-A8A5-1B2C3D4E5F60"""
            + @",__CLASS=""Msvm_VirtualEthernetSwitch""";

        Assert.Equal(
            "C08CB7B8-9B3C-4EFF-A8A5-1B2C3D4E5F60",
            CimInventory.SwitchIdOf(hostResource));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a path at all")]
    public void A_host_resource_that_names_nothing_yields_no_identifier(string? hostResource)
    {
        Assert.Null(CimInventory.SwitchIdOf(hostResource));
    }

    /// `IncludedDisks` is an array of embedded instances rendered as MOF text, and MOF text
    /// doubles every backslash.
    [Fact]
    public void The_included_disk_paths_come_out_of_the_embedded_instances()
    {
        string[] embedded =
        [
            """
            instance of Msvm_StorageAllocationSettingData
            {
                InstanceID = "Microsoft:GUID\\0\\0\\L";
                HostResource = {"D:\\VMs\\VM-DC-01\\os.vhdx"};
                ResourceSubType = "Microsoft:Hyper-V:Virtual Hard Disk";
            };
            """,
            """
            instance of Msvm_StorageAllocationSettingData
            {
                HostResource = {"D:\\VMs\\VM-DC-01\\data.vhdx"};
            };
            """,
        ];

        Assert.Equal(
            [@"D:\VMs\VM-DC-01\os.vhdx", @"D:\VMs\VM-DC-01\data.vhdx"],
            CimInventory.IncludedDiskPaths(embedded));
    }

    /// A relationship that reported nothing is not a relationship carrying no disk — the
    /// caller distinguishes the two, and this only has to not invent paths.
    [Theory]
    [InlineData(null)]
    public void No_embedded_instances_yield_no_paths(IEnumerable<string?>? embedded)
    {
        Assert.Empty(CimInventory.IncludedDiskPaths(embedded));
    }

    [Fact]
    public void An_embedded_instance_with_no_host_resource_yields_no_path()
    {
        Assert.Empty(CimInventory.IncludedDiskPaths(
            ["instance of Msvm_StorageAllocationSettingData { InstanceID = \"x\"; };", null]));
    }

    [Theory]
    [InlineData(2048UL, 2048)]
    [InlineData(0UL, 0)]
    [InlineData(null, null)]
    public void Memory_figures_are_already_megabytes(ulong? value, int? expected)
    {
        Assert.Equal(expected, CimInventory.Megabytes(value));
    }

    /// A figure past int is a provider fault rather than a machine with two petabytes of RAM.
    [Fact]
    public void A_memory_figure_past_int_reads_as_unknown()
    {
        Assert.Null(CimInventory.Megabytes((ulong)int.MaxValue + 1));
    }

    /// Only an access port has a single VLAN to compare. A trunk reads as untagged, which
    /// still compares equal across a correctly matched pair.
    [Theory]
    [InlineData((ushort)1, (ushort)10, 10)]
    [InlineData((ushort)2, (ushort)10, null)]
    [InlineData((ushort)1, null, null)]
    [InlineData(null, (ushort)10, null)]
    public void Only_an_access_port_reports_a_vlan(
        ushort? operationMode, ushort? accessVlanId, int? expected)
    {
        Assert.Equal(expected, CimInventory.AccessVlan(operationMode, accessVlanId));
    }

    /// An unread flag is not a static address: the rule has to be able to say it could not
    /// tell, rather than report a dynamic MAC that was never observed.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(null, null)]
    public void A_dynamic_mac_is_the_absence_of_a_static_one(bool? isStatic, bool? expected)
    {
        Assert.Equal(expected, CimInventory.UsesDynamicMac(isStatic));
    }

    [Theory]
    [InlineData("00155D010203", "00155D010203")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("000000000000", null)]
    public void A_mac_address_is_normalised_or_refused(string? address, string? expected)
    {
        Assert.Equal(expected, CimInventory.MacAddressOf(address));
    }

    /// Resource subtypes rather than display names, so they are not localized — the trap this
    /// project has already been bitten by twice.
    [Theory]
    [InlineData("Microsoft:Hyper-V:Virtual Hard Disk", true, false)]
    [InlineData("Microsoft:Hyper-V:Physical Disk Drive", false, true)]
    [InlineData("Microsoft:Hyper-V:Synthetic DVD Drive", false, false)]
    [InlineData(null, false, false)]
    public void The_disk_subtypes_are_told_apart(
        string? resourceSubType, bool isVhdx, bool isPhysical)
    {
        Assert.Equal(isVhdx, CimInventory.IsVirtualHardDisk(resourceSubType));
        Assert.Equal(isPhysical, CimInventory.IsPhysicalDisk(resourceSubType));
    }
}
