using Ripcord.Domain.Inventory;

namespace Ripcord.Domain.TestFailover;

/// A test VM as it exists on the host right now. Its own type rather than a
/// `VmReplicationState`: a test VM is a transient copy, it never crosses the pair channel,
/// and giving it the replicated VMs' shape would invite a rule to treat it as one.
///
/// `CreatedAt` null means the creation time could not be read; `Adapters` null means the
/// adapters could not be read. Neither is the same as the VM being new or having no network.
public sealed record TestVm(
    string Name, DateTimeOffset? CreatedAt, IReadOnlyList<VirtualAdapter>? Adapters);

/// Old enough to be the residue of an interrupted run, or too old to say.
public enum OrphanVerdict
{
    Lingering,
    AgeUnknown,
}

public sealed record Orphan(string Name, TimeSpan? Age, OrphanVerdict Verdict);

/// Test VMs nobody stopped. Hyper-V lists them and never mentions them, so an interrupted run
/// leaves disk held on the target and the next test failover fails for a reason that looks
/// unrelated.
public static class Orphans
{
    public static IReadOnlyList<Orphan> Detect(
        IReadOnlyList<TestVm> testVms, TimeSpan lingersAfter, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(testVms);

        return
        [
            .. testVms
                .Select(vm => Judge(vm, lingersAfter, now))
                .OfType<Orphan>()
                // Unknown ages first — they cannot be ranked against a dated entry, and
                // burying them under one is how they go unread.
                .OrderBy(orphan => orphan.Age.HasValue)
                .ThenByDescending(orphan => orphan.Age ?? TimeSpan.Zero)
                .ThenBy(orphan => orphan.Name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    private static Orphan? Judge(TestVm vm, TimeSpan lingersAfter, DateTimeOffset now)
    {
        if (vm.CreatedAt is not { } createdAt)
        {
            return new Orphan(vm.Name, null, OrphanVerdict.AgeUnknown);
        }

        TimeSpan age = now - createdAt;

        // A creation time ahead of now is a clock disagreement between this host and
        // whatever stamped the VM. Reading it as young would silence the report on exactly
        // the host whose clock cannot be trusted.
        if (age < TimeSpan.Zero)
        {
            return new Orphan(vm.Name, null, OrphanVerdict.AgeUnknown);
        }

        return age > lingersAfter ? new Orphan(vm.Name, age, OrphanVerdict.Lingering) : null;
    }
}
