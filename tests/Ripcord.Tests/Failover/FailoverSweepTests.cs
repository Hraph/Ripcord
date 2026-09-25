using Ripcord.Domain.Configuration;
using Ripcord.Domain.Failover;

namespace Ripcord.Tests.Failover;

/// Which VMs a sweep acts on, and — just as much — which it leaves alone and says so.
///
/// The whole point of decision D19 is that a machine can be excluded from `--all` without
/// being excluded from the tool, so "skipped" and "refused" are different outcomes here: one
/// is the configuration working as written, the other is the operator asking for something
/// that must not happen.
public class FailoverSweepTests
{
    private static readonly IReadOnlyList<VmSettings> Vms =
    [
        new VmSettings("VM-BACKUP-01", VmPriority.P2, false, true, null, null, FailoverPolicy.Manual),
        new VmSettings("VM-LEGACY-01", VmPriority.P1, false, false, null),
        new VmSettings("VM-DC-01", VmPriority.P1, true, false, null),
    ];

    /// P1 before P2, whatever order the file happens to list them in. The sweep is a failover
    /// order, not a list.
    [Fact]
    public void All_takes_every_auto_vm_in_priority_order()
    {
        SweepSelection selection = FailoverSweep.Select(Vms, SweepScope.All);

        Assert.Equal(["VM-LEGACY-01", "VM-DC-01"], selection.VmNames);
        Assert.False(selection.Refuses);
    }

    /// Silence would be the failure here: the operator typed `--all` and has to learn that one
    /// machine was not part of it, on the run itself rather than the next morning.
    [Fact]
    public void A_manual_vm_is_skipped_by_all_and_named_in_the_output()
    {
        SweepSelection selection = FailoverSweep.Select(Vms, SweepScope.All);

        SweepExclusion excluded = Assert.Single(selection.Excluded);
        Assert.Equal("VM-BACKUP-01", excluded.VmName);
        Assert.Contains("manual", excluded.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// `vms: []` is valid since init writes it on a host with no VM; a sweep of it must refuse,
    /// never exit 0 having moved nothing.
    [Fact]
    public void Sweeping_an_empty_configuration_is_refused()
    {
        SweepSelection selection = FailoverSweep.Select([], SweepScope.All);

        Assert.True(selection.Refuses);
        Assert.Empty(selection.VmNames);
    }

    [Fact]
    public void A_priority_sweep_takes_only_that_tier()
    {
        SweepSelection selection = FailoverSweep.Select(Vms, SweepScope.OfPriority(VmPriority.P1));

        Assert.Equal(["VM-LEGACY-01", "VM-DC-01"], selection.VmNames);
    }

    /// The half of D19 that makes it an exclusion rather than a ban.
    [Fact]
    public void A_manual_vm_is_taken_when_it_is_named()
    {
        SweepSelection selection =
            FailoverSweep.Select(Vms, SweepScope.Named(["VM-BACKUP-01"]));

        Assert.Equal(["VM-BACKUP-01"], selection.VmNames);
        Assert.False(selection.Refuses);
    }

    /// Named VMs are still ordered, because an operator naming three machines is asking for a
    /// failover and not for that typing order.
    [Fact]
    public void Named_vms_are_ordered_by_priority_too()
    {
        SweepSelection selection = FailoverSweep.Select(
            Vms, SweepScope.Named(["VM-BACKUP-01", "VM-DC-01"]));

        Assert.Equal(["VM-DC-01", "VM-BACKUP-01"], selection.VmNames);
    }

    [Fact]
    public void Vm_names_are_matched_case_insensitively()
    {
        SweepSelection selection = FailoverSweep.Select(Vms, SweepScope.Named(["vm-dc-01"]));

        Assert.Equal(["VM-DC-01"], selection.VmNames);
    }

    /// A typo must not quietly fail over fewer machines than were asked for.
    [Fact]
    public void An_unknown_name_refuses_the_whole_sweep()
    {
        SweepSelection selection =
            FailoverSweep.Select(Vms, SweepScope.Named(["VM-DC-01", "VM-TYPO-01"]));

        Assert.True(selection.Refuses);
        Assert.Contains(selection.Refusals, reason => reason.Contains("VM-TYPO-01"));
        Assert.Empty(selection.VmNames);
    }

    /// `never` is not "skip it": naming one is asking for something the configuration says
    /// must not happen, and skipping it silently would carry out the rest as if it had.
    [Fact]
    public void Naming_a_never_vm_refuses_the_whole_sweep()
    {
        IReadOnlyList<VmSettings> vms =
        [
            new VmSettings("VM-PINNED-01", VmPriority.P1, false, false, null, null, FailoverPolicy.Never),
            new VmSettings("VM-DC-01", VmPriority.P1, true, false, null),
        ];

        SweepSelection selection =
            FailoverSweep.Select(vms, SweepScope.Named(["VM-DC-01", "VM-PINNED-01"]));

        Assert.True(selection.Refuses);
        Assert.Empty(selection.VmNames);
    }

    [Fact]
    public void A_never_vm_is_skipped_by_all_rather_than_refusing_it()
    {
        IReadOnlyList<VmSettings> vms =
        [
            new VmSettings("VM-PINNED-01", VmPriority.P1, false, false, null, null, FailoverPolicy.Never),
            new VmSettings("VM-DC-01", VmPriority.P1, true, false, null),
        ];

        SweepSelection selection = FailoverSweep.Select(vms, SweepScope.All);

        Assert.Equal(["VM-DC-01"], selection.VmNames);
        Assert.False(selection.Refuses);
    }

    /// Exiting 0 having done nothing is the worst answer available: it reads as "production
    /// has moved".
    [Fact]
    public void A_sweep_that_matches_nothing_refuses_rather_than_succeeding_silently()
    {
        IReadOnlyList<VmSettings> vms =
        [
            new VmSettings("VM-BACKUP-01", VmPriority.P2, false, true, null, null, FailoverPolicy.Manual),
        ];

        SweepSelection selection = FailoverSweep.Select(vms, SweepScope.All);

        Assert.True(selection.Refuses);
        Assert.Single(selection.Excluded);
    }

    [Fact]
    public void A_priority_sweep_with_no_vm_in_that_tier_refuses()
    {
        SweepSelection selection = FailoverSweep.Select(Vms, SweepScope.OfPriority(VmPriority.P2));

        Assert.True(selection.Refuses);
    }
}
