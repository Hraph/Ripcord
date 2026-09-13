using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Ripcord.Domain;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;

namespace Ripcord.Adapters.Wmi;

/// The hardware `ripcord check` compares across the pair: memory, network adapters and disks.
/// Every association is named — an unnamed traversal from a VM returns a heterogeneous set,
/// and this is the deepest traversal in the project.
///
/// Written blind (no `root\virtualization\v2` off Windows) and validated on real hardware, so
/// it decides nothing: every string and number it produces goes through `CimInventory`, where
/// a test can reach it. What is left here is only "which class, which association, which
/// property", and each of those is named against the Microsoft `hyperv_v2` reference.
///
/// A VM whose settings cannot be read yields null facts rather than failing the inventory:
/// one odd VM must not cost the operator the other two.
internal static class WmiVmInventory
{
    private const string Namespace = @"root\virtualization\v2";

    /// The realized settings, not the pending ones: `Msvm_SettingsDefineState` reaches the
    /// settings that are in force, which is what the VM would boot with.
    private const string SettingsAssociation = "Msvm_SettingsDefineState";

    private const string ComponentAssociation = "Msvm_VirtualSystemSettingDataComponent";

    public static VmFacts? Read(
        CimSession session,
        CimInstance vm,
        CimInstance? relationship,
        IReadOnlyDictionary<string, string> switches,
        CimOperationOptions options)
    {
        try
        {
            using CimInstance? settings = Settings(session, vm, options);

            if (settings is null)
            {
                return null;
            }

            return new VmFacts(
                Memory(session, settings, options, "VirtualQuantity"),
                Memory(session, settings, options, "Limit"),
                Memory(session, settings, options, "Reservation"),
                Adapters(session, settings, switches, options),
                Disks(session, settings, options),
                IncludedDisks(session, settings, options, relationship));
        }
        catch (CimException)
        {
            return null;
        }
    }

    /// The adapters of a VM that has no replication relationship — a test copy. Same mapping
    /// as the replicated case, reached without one.
    public static IReadOnlyList<VirtualAdapter>? ReadAdapters(
        CimSession session,
        CimInstance vm,
        IReadOnlyDictionary<string, string> switches,
        CimOperationOptions options)
    {
        try
        {
            using CimInstance? settings = Settings(session, vm, options);

            return settings is null ? null : Adapters(session, settings, switches, options);
        }
        catch (CimException)
        {
            // Null rather than empty: "could not be read" is not "has no network", and the
            // isolation rule refuses on the first while clearing the second.
            return null;
        }
    }

    /// The live allocation instances, for the one operation that writes to them. Every other
    /// caller gets the flattened read-only view instead.
    public static IEnumerable<CimInstance> AdapterAllocations(
        CimSession session, CimInstance vm, CimOperationOptions options)
    {
        using CimInstance? settings = Settings(session, vm, options);

        return settings is null
            ? []
            : [.. Components(session, settings, "Msvm_EthernetPortAllocationSettingData", options)];
    }

    /// `Msvm_VirtualSystemSettingData.AutomaticStartupAction` — what the host does with this
    /// VM when the host itself boots. Read for fencing, which is the only thing that needs it.
    ///
    /// Null when the settings instance cannot be reached or the property is absent. It must
    /// stay null rather than fall back to `Nothing`: the fence reads this to decide whether
    /// the returning primary would boot the old domain controller.
    public static AutomaticStartAction? StartAction(
        CimSession session, CimInstance vm, CimOperationOptions options)
    {
        using CimInstance? settings = Settings(session, vm, options);

        return settings is null
            ? null
            : CimReplicationValues.StartAction(
                CimValues.Number(settings, "AutomaticStartupAction"));
    }

    /// The realized settings instance, handed to the caller to modify and pass back to
    /// `ModifySystemSettings`. Every other caller gets the flattened read-only view instead.
    public static CimInstance? MutableSettings(
        CimSession session, CimInstance vm, CimOperationOptions options) =>
        Settings(session, vm, options);

    private static CimInstance? Settings(
        CimSession session, CimInstance vm, CimOperationOptions options)
    {
        CimInstance? realized = null;

        foreach (CimInstance settings in session.EnumerateAssociatedInstances(
            Namespace,
            vm,
            SettingsAssociation,
            "Msvm_VirtualSystemSettingData",
            sourceRole: "ManagedElement",
            resultRole: "SettingData",
            options))
        {
            // Native handles: every instance is disposed, not only the one kept.
            if (realized is null)
            {
                realized = settings;
            }
            else
            {
                settings.Dispose();
            }
        }

        return realized;
    }

    /// Startup RAM, dynamic maximum and dynamic minimum all come off the same instance; the
    /// caller asks for one property at a time so the three figures stay named at the call
    /// site rather than hidden in a tuple.
    private static int? Memory(
        CimSession session,
        CimInstance settings,
        CimOperationOptions options,
        string propertyName)
    {
        foreach (CimInstance memory in Components(
            session, settings, "Msvm_MemorySettingData", options))
        {
            using (memory)
            {
                if (CimValues.Value(memory, propertyName) is ulong value)
                {
                    return CimInventory.Megabytes(value);
                }
            }
        }

        return null;
    }

    /// Two classes describe one NIC. `Msvm_SyntheticEthernetPortSettingData` holds the MAC and
    /// the name; `Msvm_EthernetPortAllocationSettingData` holds what it is plugged into. They
    /// are paired through the allocation's `Parent`, which references the port.
    ///
    /// The MAC is deliberately not read from the allocation: V6 records that
    /// `Msvm_SyntheticEthernetPortSettingData.HostResource` is always null, and the mirror of
    /// that mistake would be reading the switch from the port.
    private static List<VirtualAdapter> Adapters(
        CimSession session,
        CimInstance settings,
        IReadOnlyDictionary<string, string> switches,
        CimOperationOptions options)
    {
        List<Allocation> allocations = Allocations(session, settings, options);
        List<VirtualAdapter> adapters = [];

        foreach (CimInstance port in Components(
            session, settings, "Msvm_SyntheticEthernetPortSettingData", options))
        {
            using (port)
            {
                string instanceId = CimValues.Text(port, "InstanceID") ?? "";

                Allocation? allocation = allocations
                    .FirstOrDefault(candidate => candidate.References(instanceId));

                // Cleaned here for the same reason the peer's names are cleaned as they cross
                // the wire: these strings are printed on the console an operator reads under
                // pressure, and Hyper-V accepts an escape sequence in a name.
                adapters.Add(new VirtualAdapter(
                    CimInventory.AdapterName(CimValues.Text(port, "ElementName")),
                    Printable.OrNull(allocation?.SwitchName(switches)),
                    allocation?.IsConnected,
                    CimInventory.MacAddressOf(CimValues.Text(port, "Address")),
                    CimInventory.UsesDynamicMac(CimValues.Flag(port, "StaticMacAddress")),
                    allocation?.VlanId));
            }
        }

        return adapters;
    }

    private static List<Allocation> Allocations(
        CimSession session, CimInstance settings, CimOperationOptions options)
    {
        List<Allocation> allocations = [];

        foreach (CimInstance instance in Components(
            session, settings, "Msvm_EthernetPortAllocationSettingData", options))
        {
            using (instance)
            {
                allocations.Add(new Allocation(
                    CimValues.Text(instance, "Parent") ?? "",
                    FirstHostResource(instance),
                    Printable.OrNull(CimValues.Text(instance, "LastKnownSwitchName")),
                    Vlan(session, instance, options)));
            }
        }

        return allocations;
    }

    private static int? Vlan(
        CimSession session, CimInstance allocation, CimOperationOptions options)
    {
        foreach (CimInstance vlan in session.EnumerateAssociatedInstances(
            Namespace,
            allocation,
            // Verified against the reference: there is no
            // Msvm_EthernetPortAllocationSettingDataComponent — no such class is documented,
            // so every VLAN read returned nothing and the VLAN rule never fired.
            "Msvm_EthernetPortSettingDataComponent",
            "Msvm_EthernetSwitchPortVlanSettingData",
            sourceRole: "GroupComponent",
            resultRole: "PartComponent",
            options))
        {
            using (vlan)
            {
                return CimInventory.AccessVlan(
                    CimValues.Number(vlan, "OperationMode"),
                    CimValues.Number(vlan, "AccessVlanId"));
            }
        }

        return null;
    }

    /// A VHDX is a `Msvm_StorageAllocationSettingData`; a pass-through disk is a
    /// `Msvm_ResourceAllocationSettingData` pointing at a physical drive. Both are components
    /// of the same settings instance, and both are told apart on `ResourceSubType` — a
    /// resource subtype rather than a display name, so it is not localized.
    private static List<VmDisk> Disks(
        CimSession session, CimInstance settings, CimOperationOptions options)
    {
        List<VmDisk> disks = [];

        foreach (string className in
            new[] { "Msvm_StorageAllocationSettingData", "Msvm_ResourceAllocationSettingData" })
        {
            foreach (CimInstance instance in Components(session, settings, className, options))
            {
                using (instance)
                {
                    string? subType = CimValues.Text(instance, "ResourceSubType");
                    bool passthrough = CimInventory.IsPhysicalDisk(subType);

                    if (!passthrough && !CimInventory.IsVirtualHardDisk(subType))
                    {
                        continue;
                    }

                    if (FirstHostResource(instance) is { Length: > 0 } path)
                    {
                        disks.Add(new VmDisk(Printable.Of(path), passthrough));
                    }
                }
            }
        }

        return disks;
    }

    /// Which disks the relationship carries. Null — not an empty list — whenever it cannot be
    /// established: an empty list would report every VHDX as excluded from replication, which
    /// is the loudest possible false positive.
    ///
    /// `IncludedDisks` is an array of embedded instances rendered as MOF text, which
    /// `CimInventory` parses. V26: unverified against a real host.
    private static IReadOnlyList<string>? IncludedDisks(
        CimSession session,
        CimInstance settings,
        CimOperationOptions options,
        CimInstance? relationship)
    {
        if (relationship is null)
        {
            return null;
        }

        foreach (CimInstance replication in Components(
            session, settings, "Msvm_ReplicationSettingData", options))
        {
            using (replication)
            {
                if (CimValues.Value(replication, "IncludedDisks") is not string[] embedded)
                {
                    continue;
                }

                IReadOnlyList<string> paths = CimInventory.IncludedDiskPaths(embedded);

                // A relationship that named no disk at all is a reading that did not work,
                // not a relationship carrying nothing.
                return paths.Count > 0 ? paths : null;
            }
        }

        return null;
    }

    private static string? FirstHostResource(CimInstance instance) =>
        CimValues.Value(instance, "HostResource") is string[] { Length: > 0 } resources
            ? CimInventory.Unescape(resources[0])
            : null;

    private static IEnumerable<CimInstance> Components(
        CimSession session,
        CimInstance settings,
        string resultClassName,
        CimOperationOptions options) =>
        session.EnumerateAssociatedInstances(
            Namespace,
            settings,
            ComponentAssociation,
            resultClassName,
            sourceRole: "GroupComponent",
            resultRole: "PartComponent",
            options);

    /// One allocation, flattened. Keeping it a value rather than a live `CimInstance` means
    /// the native handles are all released before anything is compared.
    private readonly record struct Allocation(
        string Parent, string? HostResource, string? LastKnownSwitchName, int? VlanId)
    {
        /// The allocation's `Parent` is an object path ending in the port's InstanceID, so a
        /// suffix match is the pairing. Ordinal and case-insensitive: these are GUIDs.
        public bool References(string instanceId) =>
            instanceId.Length > 0
            && this.Parent.Contains(instanceId, StringComparison.OrdinalIgnoreCase);

        /// Connected when it is plugged into something. `LastKnownSwitchName` survives the
        /// disconnection, which is what makes the report readable — it names the switch the
        /// adapter *used* to be on rather than saying nothing at all.
        public bool IsConnected => this.HostResource is { Length: > 0 };

        /// Falls back to the last known name when the identifier does not resolve, so an
        /// adapter attached to a switch this inventory could not name still reports *a* name
        /// rather than reading as attached to nothing.
        public string? SwitchName(IReadOnlyDictionary<string, string> switches) =>
            CimInventory.SwitchIdOf(this.HostResource) is { } id
            && switches.TryGetValue(id, out string? name)
                ? name
                : this.LastKnownSwitchName;
    }
}
