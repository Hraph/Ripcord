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
}
