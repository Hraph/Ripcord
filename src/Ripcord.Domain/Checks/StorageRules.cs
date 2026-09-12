using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;

namespace Ripcord.Domain.Checks;

/// Disks and volumes. The two per-VM rules read the *source*'s copy — that is where the data
/// is, and where a disk gets added without being included in the relationship. The two
/// volume rules read the *target*, because that is where the VMs would have to start.
internal static class StorageRules
{
    private const long BytesPerGb = 1024L * 1024 * 1024;

    public static IEnumerable<Finding> Evaluate(CheckSubject subject)
    {
        foreach (VmSettings settings in subject.Configuration.Vms)
        {
            foreach (Finding finding in Disks(subject, settings.Name))
            {
                yield return finding;
            }
        }

        foreach (Finding finding in FreeSpace(subject))
        {
            yield return finding;
        }

        foreach (Finding finding in BitLocker(subject))
        {
            yield return finding;
        }
    }

    private static IEnumerable<Finding> Disks(CheckSubject subject, string name)
    {
        Replication.VmReplicationState? vm = CheckSubject.Find(subject.Source, name);

        if (vm?.Facts is not { } facts)
        {
            string reason = $"the disks of {name} were not readable on "
                + subject.Source.HostName;

            yield return Found.Unevaluable(
                CheckRules.PassthroughDiskOnReplicatedVm, name, reason);

            yield return Found.Unevaluable(CheckRules.VhdxOutsideRelationship, name, reason);

            yield break;
        }

        // A pass-through disk on a VM with no relationship at all is not this rule's
        // business: `vm-without-relationship` already says the louder thing about it.
        if (vm.Role != Replication.ReplicationRole.None
            && facts.PassthroughDisks.FirstOrDefault() is { } passthrough)
        {
            yield return Found.Violated(
                CheckRules.PassthroughDiskOnReplicatedVm,
                name,
                $"{name} has a pass-through disk ({passthrough.Path}) on "
                + subject.Source.HostName,
                "Hyper-V Replica cannot carry a pass-through disk: after a failover this "
                + "VM starts without that disk, and whatever lives on it is not there",
                "move the data onto a VHDX, or accept the gap and acknowledge this rule in "
                + "ripcord.yaml with a reason and an expiry");
        }

        if (!facts.KnowsWhichDisksReplicate)
        {
            yield return Found.Unevaluable(
                CheckRules.VhdxOutsideRelationship,
                name,
                $"the replication relationship of {name} did not say which disks it carries");

            yield break;
        }

        foreach (VmDisk disk in facts.DisksOutsideReplication)
        {
            yield return Found.Violated(
                CheckRules.VhdxOutsideRelationship,
                name,
                $"{disk.Path} is attached to {name} and absent from its replication "
                + "relationship",
                "this disk does not exist on the target; the VM starts there missing it, "
                + "and whatever it holds is lost for the duration of the failover",
                $"Set-VMReplication -VMName {name} -ReplicatedDisks (Get-VMHardDiskDrive "
                + $"-VMName {name})");
        }
    }

    private static IEnumerable<Finding> FreeSpace(CheckSubject subject)
    {
        StorageSettings storage = subject.Configuration.Storage;

        if (Volume(subject) is not { FreeBytes: { } free } volume)
        {
            yield return Found.Unevaluable(
                CheckRules.FreeSpaceBelowThreshold,
                null,
                $"the free space of {storage.DataVolume} on {subject.Target.HostName} "
                + "could not be read");

            yield break;
        }

        long threshold = storage.FreeSpaceWarningGb * BytesPerGb;

        if (free < threshold)
        {
            yield return Found.Violated(
                CheckRules.FreeSpaceBelowThreshold,
                null,
                $"{volume.Name} on {subject.Target.HostName} has {free / BytesPerGb} GB "
                + $"free, below the {storage.FreeSpaceWarningGb} GB threshold",
                "a failover writes on the target; if this volume fills, the VMs that "
                + "started there pause",
                $"free space on {volume.Name} of {subject.Target.HostName}, or lower "
                + "storage.free_space_warning_gb once the figure is understood");
        }
    }

    /// The one rule that has nothing to do with Hyper-V (COHERENCE B4). A protected volume
    /// that does not unlock itself needs a recovery key typed at the console — during the
    /// incident, by whoever is there.
    private static IEnumerable<Finding> BitLocker(CheckSubject subject)
    {
        if (!subject.Configuration.Storage.CheckBitLockerAutoUnlock)
        {
            yield break;
        }

        HostVolume? volume = Volume(subject);

        if (volume?.IsBitLockerProtected is not { } isProtected)
        {
            yield return Found.Unevaluable(
                CheckRules.TargetBitlockerWithoutAutounlock,
                null,
                "the BitLocker state of "
                + $"{subject.Configuration.Storage.DataVolume} on "
                + $"{subject.Target.HostName} could not be read");

            yield break;
        }

        if (!isProtected)
        {
            yield break;
        }

        if (volume.IsAutoUnlockEnabled is not { } unlocks)
        {
            yield return Found.Unevaluable(
                CheckRules.TargetBitlockerWithoutAutounlock,
                null,
                $"{volume.Name} on {subject.Target.HostName} is BitLocker-protected and "
                + "whether it unlocks itself could not be read");

            yield break;
        }

        if (!unlocks)
        {
            yield return Found.Violated(
                CheckRules.TargetBitlockerWithoutAutounlock,
                null,
                $"{volume.Name} on {subject.Target.HostName} is BitLocker-protected "
                + "without auto-unlock",
                "after a reboot of the target the volume holding the replicas stays locked: "
                + "nothing starts until a recovery key is typed at the console",
                $"Enable-BitLockerAutoUnlock -MountPoint {volume.Name} on "
                + subject.Target.HostName);
        }
    }

    private static HostVolume? Volume(CheckSubject subject) =>
        subject.Target.Facts?.Volume(subject.Configuration.Storage.DataVolume);
}
