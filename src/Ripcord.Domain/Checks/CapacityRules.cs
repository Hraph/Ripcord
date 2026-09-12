using Ripcord.Domain.Configuration;

namespace Ripcord.Domain.Checks;

/// Memory. Every figure comes from the feasibility calculation rather than being read again,
/// so the table in the report and the criticals above it can never disagree.
internal static class CapacityRules
{
    public static IEnumerable<Finding> Evaluate(CheckSubject subject, Feasibility feasibility)
    {
        foreach (Finding finding in PerVm(subject, feasibility))
        {
            yield return finding;
        }

        foreach (Finding finding in Total(subject, feasibility))
        {
            yield return finding;
        }
    }

    private static IEnumerable<Finding> PerVm(CheckSubject subject, Feasibility feasibility)
    {
        int? usable = feasibility.UsableRamMb;

        foreach (VmFeasibility vm in feasibility.Vms)
        {
            VmSettings settings = subject.Configuration.Vms
                .First(configured => configured.Name == vm.Name);

            foreach (Finding finding in Startup(subject, vm, settings, usable))
            {
                yield return finding;
            }

            foreach (Finding finding in DynamicMaximum(subject, vm, usable))
            {
                yield return finding;
            }

            foreach (Finding finding in Drift(subject, vm, settings))
            {
                yield return finding;
            }
        }
    }

    /// One VM larger than the whole target. Critical for a P1 and never acknowledgeable: a
    /// VM that cannot start is a service that does not come back, and silencing that is
    /// silencing the tool.
    private static IEnumerable<Finding> Startup(
        CheckSubject subject, VmFeasibility vm, VmSettings settings, int? usable)
    {
        if (settings.Priority != VmPriority.P1)
        {
            yield break;
        }

        if (usable is not { } capacity || vm.StartupRamMb is not { } startup)
        {
            yield return Found.Unevaluable(
                CheckRules.StartupRamExceedsTarget,
                vm.Name,
                $"the startup memory of {vm.Name} or the memory of "
                + $"{subject.Target.HostName} could not be read");

            yield break;
        }

        if (startup > capacity)
        {
            yield return Found.Violated(
                CheckRules.StartupRamExceedsTarget,
                vm.Name,
                $"{vm.Name} starts at {startup} MB; {subject.Target.HostName} has "
                + $"{capacity} MB usable",
                "this VM cannot start on the target at all, whatever else is running",
                $"Set-VMMemory -VMName {vm.Name} -StartupBytes <at most {capacity}MB>, or "
                + "add memory to the target");
        }
    }

    /// It boots, and is then squeezed (COHERENCE S5). A failover that technically succeeds
    /// and leaves a domain controller crawling is not a successful failover — a warning,
    /// because the operator must know beforehand rather than during.
    private static IEnumerable<Finding> DynamicMaximum(
        CheckSubject subject, VmFeasibility vm, int? usable)
    {
        if (usable is not { } capacity || vm.DynamicMaximumMb is not { } maximum)
        {
            yield return Found.Unevaluable(
                CheckRules.DynamicMaximumExceedsTarget,
                vm.Name,
                $"the dynamic maximum of {vm.Name} or the memory of "
                + $"{subject.Target.HostName} could not be read");

            yield break;
        }

        // A maximum at or below the startup figure is a VM that cannot grow, whether or not
        // dynamic memory is switched on. Nothing to warn about, and warning anyway would
        // fire on every VM with static memory.
        if (vm.StartupRamMb is { } startup && maximum <= startup)
        {
            yield break;
        }

        if (maximum > capacity)
        {
            yield return Found.Violated(
                CheckRules.DynamicMaximumExceedsTarget,
                vm.Name,
                $"{vm.Name} may grow to {maximum} MB; {subject.Target.HostName} has "
                + $"{capacity} MB usable"
                + (vm.DynamicMinimumMb is { } minimum
                    ? $", and this VM can be squeezed to {minimum} MB"
                    : ""),
                "this VM starts on the target and is then squeezed; it runs, but not "
                + "necessarily usefully",
                $"Set-VMMemory -VMName {vm.Name} -MaximumBytes <at most {capacity}MB>");
        }
    }

    /// The configured figure was written in calm conditions; the observed one is what is true
    /// on the day (decision D5). A difference means the VM was resized and nobody updated the
    /// file — which makes every other number in it suspect.
    private static IEnumerable<Finding> Drift(
        CheckSubject subject, VmFeasibility vm, VmSettings settings)
    {
        if (settings.ExpectedStartupRamMb is not { } expected)
        {
            yield break;
        }

        if (vm.StartupRamMb is not { } observed)
        {
            yield return Found.Unevaluable(
                CheckRules.StartupRamDrift,
                vm.Name,
                $"the startup memory of {vm.Name} could not be read on "
                + subject.Target.HostName);

            yield break;
        }

        if (observed != expected)
        {
            yield return Found.Violated(
                CheckRules.StartupRamDrift,
                vm.Name,
                $"{vm.Name} starts at {observed} MB; the configuration expects {expected} MB",
                "the VM has been resized since the configuration was written, so the rest "
                + "of that file is worth re-reading too",
                $"set vms[].expected_startup_ram_mb to {observed}, or resize the VM");
        }
    }

    /// The sum, not the individuals: three VMs that each fit and together do not is the case
    /// a per-VM rule cannot see. Never acknowledgeable, for the same reason.
    private static IEnumerable<Finding> Total(CheckSubject subject, Feasibility feasibility)
    {
        if (feasibility.P1FitsOnTheTarget is not { } fits)
        {
            yield return Found.Unevaluable(
                CheckRules.P1StartupRamSumExceedsTarget,
                null,
                "the P1 startup memory or the memory of "
                + $"{subject.Target.HostName} could not be read in full");

            yield break;
        }

        if (!fits)
        {
            yield return Found.Violated(
                CheckRules.P1StartupRamSumExceedsTarget,
                null,
                $"the P1 VMs need {feasibility.P1StartupRamMb} MB; "
                + $"{subject.Target.HostName} has {feasibility.UsableRamMb} MB usable",
                "not every P1 VM can run on the target at once; the feasibility table below "
                + "says which one would be refused",
                "add memory to the target, lower a startup figure, or accept that the last "
                + "VM in failover order stays down");
        }
    }
}
