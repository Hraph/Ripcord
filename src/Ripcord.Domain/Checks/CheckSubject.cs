using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;

namespace Ripcord.Domain.Checks;

/// Whether the pair is running as designed, or already on the disaster recovery side.
/// Derived from observed state and never from configuration (decision D20) — the baseline it
/// is compared against is `replication.expected_role`, which is what the pair is *meant* to
/// look like, not what it is.
public enum OperatingMode
{
    Normal,
    FailedOver,
}

/// The pair as the rules see it. Source normally holds the primary copies and Target normally
/// holds the replicas, both assigned from `replication.expected_role` — so "target" means
/// "where a failover would land", which does not change just because a failover has happened.
///
/// Assembled once, because every rule needs the same answer to "which of these two hosts is
/// which", and two rules disagreeing on that would be invisible in the output.
public sealed record CheckSubject(
    HostState Source,
    HostState Target,
    bool TargetIsLocal,
    OperatingMode Mode,
    RipcordConfiguration Configuration,
    DateTimeOffset Now)
{
    /// The host this command is running on. Its own replication figures are always readable,
    /// which is why the per-VM health, lag and resynchronization rules use them.
    public HostState Local => this.TargetIsLocal ? this.Target : this.Source;

    public static CheckSubject From(
        PairView view, RipcordConfiguration configuration, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(configuration);

        bool targetIsLocal = configuration.Replication.ExpectedRole == ExpectedRole.Replica;

        HostState target = targetIsLocal ? view.Local : view.Peer;
        HostState source = targetIsLocal ? view.Peer : view.Local;

        return new CheckSubject(
            source, target, targetIsLocal, ModeOf(target, configuration), configuration, now);
    }

    /// Failed over when a P1 primary copy is running on the host that normally holds the
    /// replicas. A target that cannot be read says nothing either way, and "nothing either
    /// way" is normal operation — if this host is the primary and the DR host is offline, the
    /// pair is not failed over, it is degraded.
    private static OperatingMode ModeOf(HostState target, RipcordConfiguration configuration)
    {
        if (!target.IsReachable)
        {
            return OperatingMode.Normal;
        }

        bool anyPrimary = configuration.Vms
            .Where(vm => vm.Priority == VmPriority.P1)
            .Any(vm => Find(target, vm.Name)?.Role == ReplicationRole.Primary);

        return anyPrimary ? OperatingMode.FailedOver : OperatingMode.Normal;
    }

    /// VM names are matched case-insensitively: Hyper-V preserves the case it was given and
    /// the configuration was typed by hand.
    public static VmReplicationState? Find(HostState host, string name) =>
        host.Vms.FirstOrDefault(vm =>
            string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase));
}
