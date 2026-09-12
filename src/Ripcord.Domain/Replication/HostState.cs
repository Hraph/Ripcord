using Ripcord.Domain.Inventory;

namespace Ripcord.Domain.Replication;

/// One side of the pair at one instant. An unreachable host carries no VMs: an empty
/// inventory and an unknown inventory must never look alike.
///
/// Facts is null when the host system was not read — a peer publishing an older wire schema,
/// or a reading that failed. `check` reports that as "cannot evaluate", never as success.
public sealed record HostState(
    string HostName,
    IReadOnlyList<VmReplicationState> Vms,
    HostReachability Reachability,
    HostFacts? Facts = null)
{
    public static HostState Unreachable(string hostName, HostReachability reachability) =>
        new(hostName, [], reachability);

    public bool IsReachable => Reachability.IsReachable;

    /// A record holding a list compares it by reference, which makes two identical host states
    /// unequal — the wire round-trip trips on it first, cross-host comparison would be next.
    public bool Equals(HostState? other) =>
        other is not null
        && HostName == other.HostName
        && Reachability == other.Reachability
        && Facts == other.Facts
        && Vms.SequenceEqual(other.Vms);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(HostName);
        hash.Add(Reachability);
        hash.Add(Facts);

        foreach (VmReplicationState vm in Vms)
        {
            hash.Add(vm);
        }

        return hash.ToHashCode();
    }
}
