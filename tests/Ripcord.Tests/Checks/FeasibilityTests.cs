using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Checks;

/// The feasibility calculation, verified by hand — which is the milestone's exit criterion.
/// Every figure comes from the target's own copy of the VM, and anything unreadable stays
/// unknown: a sum computed over the VMs that happened to be readable understates the need,
/// and an understated need is how the critical rule silently passes.
public class FeasibilityTests
{
    [Fact]
    public void Usable_memory_is_the_targets_physical_ram_less_the_reserve()
    {
        Assert.Equal(8192, Calculate().UsableRamMb);
    }

    /// Three VMs at 2 GB against 8 GB usable: all three boot, 2 GB is left, and the P1 sum
    /// counts the two P1 VMs only.
    [Fact]
    public void Every_vm_that_fits_boots_and_the_headroom_is_what_is_left()
    {
        Feasibility feasibility = Calculate();

        Assert.All(feasibility.Vms, vm => Assert.True(vm.WouldBoot));
        Assert.Equal(4096, feasibility.P1StartupRamMb);
        Assert.Equal(2048, feasibility.HeadroomMb);
    }

    /// Three VMs at 3 GB against 8 GB usable: the first two boot, the third is refused.
    [Fact]
    public void A_vm_that_does_not_fit_is_refused_and_consumes_nothing()
    {
        Feasibility feasibility = Calculate(startup: 3072);

        Assert.Equal([true, true, false], feasibility.Vms.Select(vm => vm.WouldBoot));
        Assert.Equal(2048, feasibility.HeadroomMb);
    }

    /// Refusing the big one must not refuse the small one behind it — that is what an
    /// operator would do by hand, and the report has to say the same thing.
    [Fact]
    public void A_smaller_vm_behind_a_refused_one_still_boots()
    {
        Feasibility feasibility = Calculate(
            perVmStartup: new Dictionary<string, int>
            {
                ["VM-DC-01"] = 2048,
                ["VM-LEGACY-01"] = 7168,
                ["VM-BACKUP-01"] = 1024,
            });

        Assert.Equal(
            [("VM-DC-01", (bool?)true), ("VM-LEGACY-01", false), ("VM-BACKUP-01", true)],
            feasibility.Vms.Select(vm => (vm.Name, vm.WouldBoot)));
    }

    /// P1 before P2, then by name — so the answer does not depend on the order WMI happened
    /// to enumerate the VMs, and is the same on both hosts.
    [Fact]
    public void Allocation_follows_failover_order_not_enumeration_order()
    {
        Assert.Equal(
            ["VM-DC-01", "VM-LEGACY-01", "VM-BACKUP-01"],
            Calculate().Vms.Select(vm => vm.Name));
    }

    [Fact]
    public void The_p1_sum_over_the_capacity_is_the_rules_verdict()
    {
        Assert.True(Calculate(startup: 2048).P1FitsOnTheTarget);
        Assert.False(Calculate(startup: 5120).P1FitsOnTheTarget);
    }

    /// A target whose memory could not be read yields unknowns everywhere rather than a pass.
    [Fact]
    public void An_unreadable_target_yields_no_verdict_at_all()
    {
        Feasibility feasibility = Calculate(physicalRamMb: null);

        Assert.Null(feasibility.UsableRamMb);
        Assert.Null(feasibility.HeadroomMb);
        Assert.Null(feasibility.P1FitsOnTheTarget);
        Assert.All(feasibility.Vms, vm => Assert.Null(vm.WouldBoot));
    }

    /// One missing figure makes the whole P1 sum unknown, not a smaller sum.
    [Fact]
    public void One_unreadable_p1_vm_makes_the_p1_sum_unknown()
    {
        Feasibility feasibility = Calculate(
            perVmStartup: new Dictionary<string, int> { ["VM-BACKUP-01"] = 2048 });

        Assert.Null(feasibility.P1StartupRamMb);
        Assert.Null(feasibility.P1FitsOnTheTarget);
        Assert.Equal([null, null, true], feasibility.Vms.Select(vm => vm.WouldBoot));
    }

    /// A configured VM the target has never heard of cannot be sized. Reporting it as fitting
    /// would be the reassuring false negative this whole milestone exists to prevent.
    [Fact]
    public void A_configured_vm_absent_from_the_target_cannot_be_sized()
    {
        Feasibility feasibility = Calculate(present: ["VM-DC-01"]);
        VmFeasibility missing = feasibility.Vms.Single(vm => vm.Name == "VM-LEGACY-01");

        Assert.Null(missing.StartupRamMb);
        Assert.Null(missing.WouldBoot);
    }

    /// The dynamic window travels with the startup figure (COHERENCE S5): a domain controller
    /// squeezed to its minimum on a 12 GB target has failed over, but not usefully.
    [Fact]
    public void The_dynamic_window_is_reported_beside_the_startup_figure()
    {
        VmFeasibility vm = Calculate().Vms[0];

        Assert.Equal(4096, vm.DynamicMaximumMb);
        Assert.Equal(1024, vm.DynamicMinimumMb);
    }

    private static Feasibility Calculate(
        int startup = 2048,
        Dictionary<string, int>? perVmStartup = null,
        int? physicalRamMb = 12288,
        IReadOnlyList<string>? present = null)
    {
        RipcordConfiguration configuration = Configurations.Create();

        IReadOnlyList<string> names = present ?? [.. configuration.Vms.Select(vm => vm.Name)];

        List<VmReplicationState> vms =
        [
            .. names.Select(name => Vm(name, Startup(name, startup, perVmStartup))),
        ];

        HostState target = new(
            ValidDocument.MachineName,
            vms,
            HostReachability.Reachable(),
            physicalRamMb is null ? null : new HostFacts(physicalRamMb, [], null));

        return Feasibility.Calculate(
            configuration.Vms, target, configuration.Node.HostMemoryReserveGb);
    }

    /// Absent from an explicit per-VM table means "this figure could not be read".
    private static int? Startup(
        string name, int startup, Dictionary<string, int>? perVmStartup) =>
        perVmStartup is null
            ? startup
            : perVmStartup.TryGetValue(name, out int declared) ? declared : null;

    private static VmReplicationState Vm(string name, int? startupRamMb) =>
        new(
            name,
            ReplicationRole.Replica,
            ReplicationState.Replicating,
            ReplicationHealth.Normal,
            null,
            null,
            new VmFacts(startupRamMb, 4096, 1024, [], [], null));
}
