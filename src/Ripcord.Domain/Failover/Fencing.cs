using Ripcord.Domain.Replication;

namespace Ripcord.Domain.Failover;

/// One VM to fence, and the setting to put back afterwards.
///
/// `Previous` is recorded rather than assumed because `reprotect` has to restore it: a host
/// that comes home from a disaster with every VM set to never start is a second outage waiting
/// for the next reboot.
public sealed record FenceAction(string VmName, AutomaticStartAction Previous)
{
    public bool PreviousIsKnown => this.Previous != AutomaticStartAction.Unknown;
}

/// What fencing the returning host involves: what to change, what is already safe, and what
/// stops it.
public sealed record FencePlan(
    IReadOnlyList<FenceAction> ToFence,
    IReadOnlyList<string> AlreadyFenced,
    IReadOnlyList<string> Absent,
    IReadOnlyList<string> NotConfirmedOff,
    string? Halt)
{
    public bool HasWork => this.ToFence.Count > 0;

    public bool Equals(FencePlan? other) =>
        other is not null
        && Structural.Same(this.ToFence, other.ToFence)
        && Structural.Same(this.AlreadyFenced, other.AlreadyFenced)
        && Structural.Same(this.Absent, other.Absent)
        && Structural.Same(this.NotConfirmedOff, other.NotConfirmedOff)
        && this.Halt == other.Halt;

    public override int GetHashCode()
    {
        HashCode hash = new();
        Structural.Add(ref hash, this.ToFence);
        Structural.Add(ref hash, this.AlreadyFenced);
        Structural.Add(ref hash, this.Absent);
        Structural.Add(ref hash, this.NotConfirmedOff);
        hash.Add(this.Halt);
        return hash.ToHashCode();
    }
}

/// Decides what has to happen to the original primary the moment it is reachable again after
/// an unplanned failover.
///
/// Scoped to the VMs that were failed over and to nothing else. The returning host may carry
/// machines nobody moved, and turning off the auto-start of one of those is a change made to
/// production for no reason.
public static class Fencing
{
    public static FencePlan Plan(
        IReadOnlyList<VmReplicationState> onOriginalPrimary, IReadOnlyList<string> failedOver)
    {
        ArgumentNullException.ThrowIfNull(onOriginalPrimary);
        ArgumentNullException.ThrowIfNull(failedOver);

        List<VmReplicationState> subjects =
        [
            .. failedOver
                .Select(name => onOriginalPrimary.FirstOrDefault(vm => Same(vm.Name, name)))
                .OfType<VmReplicationState>(),
        ];

        List<string> absent =
        [
            .. failedOver.Where(name =>
                !onOriginalPrimary.Any(vm => Same(vm.Name, name))),
        ];

        // Fencing prevents a *future* boot. A copy already running here is not a future boot,
        // it is the divergence under way — and setting a start action would leave it running
        // and report success. Nothing else is attempted while that is true.
        if (subjects.Where(Live).Select(vm => vm.Name).ToList() is { Count: > 0 } live)
        {
            return new FencePlan(
                [],
                [],
                absent,
                [],
                $"{string.Join(", ", live)} is already running on this host while the pair is "
                    + "failed over; two live copies is the state no sequence here repairs");
        }

        // An unread setting is fenced all the same. The trade-off is deliberate: a host that
        // boots the old domain controller is worse than a restore value nobody has, and the
        // cost — `reprotect` cannot put this one back — is reported rather than absorbed.
        List<FenceAction> toFence =
        [
            .. subjects
                .Where(vm => vm.StartAction != AutomaticStartAction.Nothing)
                .Select(vm => new FenceAction(
                    vm.Name, vm.StartAction ?? AutomaticStartAction.Unknown)),
        ];

        List<string> alreadyFenced =
        [
            .. subjects
                .Where(vm => vm.StartAction == AutomaticStartAction.Nothing)
                .Select(vm => vm.Name),
        ];

        // Saved and paused are neither running nor off: both resume into a second live
        // claimant. Named, so that "fenced" is never read as "safe".
        List<string> notConfirmedOff =
        [
            .. subjects.Where(vm => vm.PowerState != VmPowerState.Off).Select(vm => vm.Name),
        ];

        return new FencePlan(toFence, alreadyFenced, absent, notConfirmedOff, null);
    }

    /// Which VMs the other host has taken, read off that host rather than remembered here.
    /// A copy serving after a failover, or simply running where a replica should be idle, is
    /// one that moved — and either reading is enough, because a stale relationship state and a
    /// running machine are different ways of seeing the same claim.
    public static IReadOnlyList<string> FailedOver(IReadOnlyList<VmReplicationState> onPeer)
    {
        ArgumentNullException.ThrowIfNull(onPeer);

        return
        [
            .. onPeer
                .Where(vm =>
                    vm.State is ReplicationState.Recovered or ReplicationState.Committed
                    || Live(vm))
                .Select(vm => vm.Name),
        ];
    }

    private static bool Live(VmReplicationState vm) =>
        vm.PowerState is VmPowerState.Running or VmPowerState.Starting;

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
