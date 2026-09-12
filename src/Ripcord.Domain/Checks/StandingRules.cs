using Ripcord.Domain.Configuration;

namespace Ripcord.Domain.Checks;

/// Facts about this infrastructure that are true whatever WMI says. They are stated every
/// run, as information rather than as faults: each one changes what the operator would do on
/// the day, and none of them can be fixed by a command.
internal static class StandingRules
{
    /// The point at which "support ends one day" becomes something to plan.
    private static readonly TimeSpan SupportHorizon = TimeSpan.FromDays(180);

    public static IEnumerable<Finding> Evaluate(CheckSubject subject)
    {
        foreach (VmSettings vm in subject.Configuration.Vms)
        {
            if (vm.HasPassthroughDisk)
            {
                yield return Found.Violated(
                    CheckRules.PassthroughDiskIsSolePointInTimeCopy,
                    vm.Name,
                    $"{vm.Name} holds a pass-through disk, which Hyper-V Replica does not "
                    + "carry",
                    "with no recovery history the replica is a 30-second mirror, so that "
                    + "disk is the whole of the point-in-time protection in this "
                    + "infrastructure - and it is the one disk that does not cross",
                    null);
            }

            if (vm.IsDomainController)
            {
                yield return Found.Violated(
                    CheckRules.DomainControllerPresent,
                    vm.Name,
                    $"{vm.Name} is a domain controller",
                    "it must come up first in a real failover, and a test failover of it "
                    + "has to stay isolated and stopped or the domain risks a USN rollback",
                    null);
            }

            foreach (Finding finding in GuestSupport(subject, vm))
            {
                yield return finding;
            }
        }

        if (subject.Mode == OperatingMode.FailedOver)
        {
            yield return Found.Violated(
                CheckRules.NoBackupsWhileFailedOver,
                null,
                $"the pair is running on {subject.Target.HostName}, the disaster recovery "
                + "side",
                "no backups run while failed over, and the disaster recovery window is "
                + "when they are most wanted",
                null);
        }
    }

    /// The date is configuration because nothing observable knows it: no WMI class reports
    /// when Microsoft stops shipping patches for a guest.
    private static IEnumerable<Finding> GuestSupport(CheckSubject subject, VmSettings vm)
    {
        if (vm.GuestOsSupportEnds is not { } ends)
        {
            yield break;
        }

        if (subject.Now >= ends)
        {
            yield return Found.Violated(
                CheckRules.GuestOsOutOfSupport,
                vm.Name,
                $"the guest operating system of {vm.Name} left support on {ends:yyyy-MM-dd}",
                "no security patches: this VM is a standing risk whether the pair fails "
                + "over or not",
                null);
        }
        else if (ends - subject.Now <= SupportHorizon)
        {
            yield return Found.Violated(
                CheckRules.GuestOsOutOfSupport,
                vm.Name,
                $"the guest operating system of {vm.Name} leaves support on "
                + $"{ends:yyyy-MM-dd}, in {(int)(ends - subject.Now).TotalDays} days",
                "migrating a guest takes longer than the notice period; the decision is due "
                + "before the date, not on it",
                null);
        }
    }
}
