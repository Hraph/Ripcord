namespace Ripcord.Domain.Inventory;

/// Everything `ripcord check` needs about a VM beyond its replication state: what it would
/// boot with on the target. Null anywhere means "not read", which is a third answer next to
/// satisfied and violated — a rule whose data is missing must say so, never conclude success.
///
/// Collected on both hosts and carried in the published snapshot, because most of the rules
/// compare the two sides.
public sealed record VmFacts(
    int? StartupRamMb,
    int? DynamicMaximumMb,
    int? DynamicMinimumMb,
    IReadOnlyList<VirtualAdapter> Adapters,
    IReadOnlyList<VmDisk> Disks,
    IReadOnlyList<string>? ReplicatedDiskPaths)
{
    /// Null, not empty: an unread relationship would otherwise report every VHDX as excluded.
    public bool KnowsWhichDisksReplicate => this.ReplicatedDiskPaths is not null;

    public IEnumerable<VmDisk> PassthroughDisks => this.Disks.Where(disk => disk.IsPassthrough);

    /// A VHDX attached to the VM and absent from the relationship will not exist on the
    /// target. Pass-through disks are excluded: Hyper-V Replica cannot carry one at all, and
    /// they have their own, louder rule.
    public IEnumerable<VmDisk> DisksOutsideReplication =>
        this.ReplicatedDiskPaths is { } replicated
            ? this.Disks.Where(disk =>
                !disk.IsPassthrough
                && !replicated.Contains(disk.Path, StringComparer.OrdinalIgnoreCase))
            : [];

    public bool Equals(VmFacts? other) =>
        other is not null
        && this.StartupRamMb == other.StartupRamMb
        && this.DynamicMaximumMb == other.DynamicMaximumMb
        && this.DynamicMinimumMb == other.DynamicMinimumMb
        && Structural.Same(this.Adapters, other.Adapters)
        && Structural.Same(this.Disks, other.Disks)
        && Structural.Same(this.ReplicatedDiskPaths, other.ReplicatedDiskPaths);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(this.StartupRamMb);
        hash.Add(this.DynamicMaximumMb);
        hash.Add(this.DynamicMinimumMb);
        Structural.Add(ref hash, this.Adapters);
        Structural.Add(ref hash, this.Disks);
        Structural.Add(ref hash, this.ReplicatedDiskPaths);
        return hash.ToHashCode();
    }
}

/// One virtual NIC. `SwitchName` null means bound to no switch; `IsConnected` null means the
/// binding could not be read (V6 — which CIM class reports which is not settled). The two are
/// different answers and the rules treat them differently.
public sealed record VirtualAdapter(
    string Name,
    string? SwitchName,
    bool? IsConnected,
    string? MacAddress,
    bool? UsesDynamicMac,
    int? VlanId);

/// A disk as attached to the VM. A pass-through disk carries a device path rather than a
/// file path; it is still the string the relationship would have to name.
public sealed record VmDisk(string Path, bool IsPassthrough);
