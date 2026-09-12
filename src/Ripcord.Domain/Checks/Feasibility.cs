using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;

namespace Ripcord.Domain.Checks;

/// "If it goes down now, does it hold?" — expressed as arithmetic rather than as a verdict.
/// Startup RAM against the target's physical memory less the host reserve, VM by VM, with an
/// explicit answer for each: it would boot, it would be refused, or it cannot be said.
///
/// Every figure comes from the *target*'s copy of the VM, because that is the configuration it
/// would actually boot with. A target that cannot be read yields unknowns, never a pass.
public sealed record Feasibility(
    int? UsableRamMb,
    int? P1StartupRamMb,
    int? HeadroomMb,
    IReadOnlyList<VmFeasibility> Vms)
{
    /// Sum and capacity both known, and the sum over the capacity: the one shape that makes
    /// the critical rule true. Anything unknown is unevaluable, not a pass.
    public bool? P1FitsOnTheTarget =>
        this.UsableRamMb is { } usable && this.P1StartupRamMb is { } needed
            ? needed <= usable
            : null;

    public static Feasibility Calculate(
        IReadOnlyList<VmSettings> configured, HostState target, int hostReserveGb)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(target);

        int? usable = target.Facts?.UsableRamMb(hostReserveGb);
        int allocated = 0;

        // Once one VM's startup figure is missing, nothing after it in failover order can be
        // answered either: that VM will consume an unknown amount of the target, so a later
        // "it would boot" would be arithmetic over a number nobody read.
        bool capacityKnown = true;

        // Allocation order is failover order: P1 before P2, then by name so the answer is the
        // same on both hosts and from one run to the next.
        List<VmFeasibility> vms = [];

        foreach (VmSettings settings in configured
            .OrderBy(vm => vm.Priority)
            .ThenBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase))
        {
            VmFacts? facts = target.Vms
                .FirstOrDefault(vm => string.Equals(
                    vm.Name, settings.Name, StringComparison.OrdinalIgnoreCase))
                ?.Facts;

            bool? wouldBoot = null;

            if (facts?.StartupRamMb is not { } startup)
            {
                capacityKnown = false;
            }
            else if (usable is { } capacity && capacityKnown)
            {
                wouldBoot = allocated + startup <= capacity;

                // A VM that would be refused consumes nothing: the next, smaller one may
                // still start, and that is what the operator would do by hand.
                if (wouldBoot.Value)
                {
                    allocated += startup;
                }
            }

            vms.Add(new VmFeasibility(
                settings.Name,
                settings.Priority,
                facts?.StartupRamMb,
                facts?.DynamicMaximumMb,
                facts?.DynamicMinimumMb,
                wouldBoot));
        }

        return new Feasibility(
            usable,
            P1Startup(vms),
            capacityKnown ? Headroom(usable, allocated) : null,
            vms);
    }

    /// Null as soon as one P1 figure is missing. A sum over the VMs that *could* be read would
    /// be understated, and an understated sum is how the critical rule silently passes.
    private static int? P1Startup(List<VmFeasibility> vms)
    {
        int total = 0;

        foreach (VmFeasibility vm in vms.Where(vm => vm.Priority == VmPriority.P1))
        {
            if (vm.StartupRamMb is not { } startup)
            {
                return null;
            }

            total += startup;
        }

        return total;
    }

    private static int? Headroom(int? usable, int allocated) =>
        usable is { } capacity ? capacity - allocated : null;

    public bool Equals(Feasibility? other) =>
        other is not null
        && this.UsableRamMb == other.UsableRamMb
        && this.P1StartupRamMb == other.P1StartupRamMb
        && this.HeadroomMb == other.HeadroomMb
        && Structural.Same(this.Vms, other.Vms);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(this.UsableRamMb);
        hash.Add(this.P1StartupRamMb);
        hash.Add(this.HeadroomMb);
        Structural.Add(ref hash, this.Vms);
        return hash.ToHashCode();
    }
}

/// One VM's line in the report. The dynamic maximum and minimum are shown beside the startup
/// figure (COHERENCE S5): a VM that boots and is then squeezed to its minimum has technically
/// failed over, which is not the same as having failed over usefully.
public sealed record VmFeasibility(
    string Name,
    VmPriority Priority,
    int? StartupRamMb,
    int? DynamicMaximumMb,
    int? DynamicMinimumMb,
    bool? WouldBoot);
