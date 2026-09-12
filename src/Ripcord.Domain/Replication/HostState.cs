namespace Ripcord.Domain.Replication;

/// One side of the pair at one instant. An unreachable host carries no VMs: an empty
/// inventory and an unknown inventory must never look alike.
public sealed record HostState(
    string HostName,
    IReadOnlyList<VmReplicationState> Vms,
    HostReachability Reachability)
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
        && Vms.SequenceEqual(other.Vms);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(HostName);
        hash.Add(Reachability);

        foreach (VmReplicationState vm in Vms)
        {
            hash.Add(vm);
        }

        return hash.ToHashCode();
    }
}
