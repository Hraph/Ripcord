using Ripcord.Domain.Configuration;

namespace Ripcord.Domain.Deployment;

/// The publishing service as `service install` finds it: its own service, what it was granted,
/// its place in Hyper-V Administrators and on the BitLocker namespace.
public sealed record ObservedPublisher(
    ObservedService Service,
    bool ConfigurationReadable,
    bool SnapshotWritable,
    bool LogsWritable,

    /// Whether the listener can write the publisher's logs folder, inherited from `logs`.
    bool ListenerCanWriteItsLogs,
    bool InstallFolderGranted,

    /// This host's name for S-1-5-32-578; null when there is no such group.
    string? HyperVAdministrators,

    /// Null when the membership could not be told.
    bool? InHyperVAdministrators,

    /// Null when the namespace is absent or unreadable; `EncryptionNote` then says which.
    NamespaceGrant? Encryption,
    string? EncryptionNote,

    /// Whether Windows restarts it after a crash (`ServiceRecovery`).
    bool Recovers = false,

    /// Running another build than the binary on disk, as its process record says.
    bool Outdated = false)
{
    /// None of it there. The group's name is this host's to give, never assumed: null, as when
    /// it could not be found, blocks an install rather than naming a group that is not there.
    public static ObservedPublisher Absent(string? hyperVAdministrators) =>
        new(ObservedService.Absent, false, false, false, false, false, hyperVAdministrators, false, NamespaceGrant.Missing, null);
}

/// What the publishing service needs, in the order it needs it. Least privilege, named step by
/// step: Hyper-V Administrators to read the VMs, modify on the snapshot folder and on its own
/// logs folder and nowhere else, and BitLocker only while the configuration checks it.
public static class PublisherSteps
{
    private static readonly RipcordService Publisher = RipcordService.Publisher;

    public const string NoHyperVAdministrators =
        "this host has no Hyper-V Administrators group (S-1-5-32-578): the publishing service "
        + "could not read Hyper-V. Is the Hyper-V role installed?";

    public static string MembershipUnreadable(string group) =>
        $"whether {Publisher.Account} is in '{group}' could not be read: run "
        + $"net localgroup \"{group}\" as an administrator to see why";

    public static IEnumerable<DeploymentStep> Create(DesiredDeployment desired, ObservedPublisher observed)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(observed);

        if (!observed.Service.Installed)
        {
            yield return Step(
                DeploymentAction.CreateService,
                $"Create the '{Publisher.Name}' service running '{desired.BinaryPath} {Publisher.Verb}' "
                    + $"as {Publisher.Account}",
                "it republishes this host's snapshot every 15 s; nothing else writes it unasked");
        }

        if (!observed.Recovers)
        {
            yield return Recovery(Publisher);
        }
    }

    /// Shared with the listener's plan, so both services recover the same way.
    public static DeploymentStep Recovery(RipcordService service) =>
        new(
            service,
            DeploymentAction.ConfigureRecovery,
            $"Restart '{service.Name}' a minute after a crash (sc.exe {ServiceRecovery.Arguments(service)})",
            "a service that died stays dead until somebody notices; one that stops itself on "
                + "purpose is left alone");

    public static IEnumerable<DeploymentStep> Grants(DesiredDeployment desired, ObservedPublisher observed)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(observed);

        if (!observed.ConfigurationReadable)
        {
            yield return Step(
                DeploymentAction.GrantConfigurationAccess,
                $"Grant {Publisher.Account} read access to the files in '{desired.InstallFolder}'",
                "it reads ripcord.yaml there at every start; nothing below it, nothing written");
        }

        if (!observed.SnapshotWritable)
        {
            yield return Step(
                DeploymentAction.GrantSnapshotWriteAccess,
                $"Grant {Publisher.Account} modify access to '{desired.SnapshotFolder}'",
                "it rewrites the snapshot there; that folder holds nothing else");
        }

        if (!observed.LogsWritable || observed.ListenerCanWriteItsLogs)
        {
            yield return Step(
                DeploymentAction.GrantLogsAccess,
                $"Create '{desired.PublisherLogsFolder}', grant {Publisher.Account} modify "
                    + $"access to it and keep {RipcordService.Listener.Account} out",
                "the listener faces the network and must not be able to rewrite this log");
        }

        if (observed.InHyperVAdministrators != true)
        {
            yield return Step(
                DeploymentAction.AddToHyperVAdministrators,
                $"Add {Publisher.Account} to '{observed.HyperVAdministrators}'",
                "reading the VMs' replication needs it; the listener never gets it",
                observed.HyperVAdministrators);
        }

        if (desired.CheckBitLocker && observed.Encryption == NamespaceGrant.Missing)
        {
            yield return Step(
                DeploymentAction.GrantEncryptionNamespaceAccess,
                $"Let {Publisher.Account} read BitLocker state: add "
                    + NamespaceAcl.Ace(VirtualAccount.Sid(Publisher.Name)),
                "storage.check_bitlocker_autounlock is on, and that namespace is admin-only");
        }
        else if (!desired.CheckBitLocker && observed.Encryption == NamespaceGrant.Granted)
        {
            yield return Step(
                DeploymentAction.RevokeEncryptionNamespaceAccess,
                $"Take back {Publisher.Account}'s access to BitLocker state",
                "storage.check_bitlocker_autounlock is off");
        }
    }

    /// Started, repointed, or restarted when it was just added to the group: membership only
    /// reaches a process at its next start.
    public static IEnumerable<DeploymentStep> Starts(DesiredDeployment desired, ObservedPublisher observed)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(observed);

        ObservedService service = observed.Service;

        if (service.Installed && !SamePath(service.BinaryPath, desired.BinaryPath))
        {
            yield return Step(
                DeploymentAction.UpdateService,
                $"Point the '{Publisher.Name}' service at '{desired.BinaryPath} {Publisher.Verb}'",
                $"it currently runs '{service.BinaryPath}'");
        }
        else if (!service.Installed || service.State != ServiceRunState.Running)
        {
            yield return Step(
                DeploymentAction.StartService,
                $"Start the '{Publisher.Name}' service",
                "a publisher that is not running leaves the peer a stale snapshot");
        }
        else if (observed.InHyperVAdministrators == false)
        {
            yield return Step(
                DeploymentAction.RestartService,
                $"Restart the '{Publisher.Name}' service",
                "it was just added to the group, which it sees only from its next start");
        }
        else if (observed.Outdated)
        {
            yield return Step(
                DeploymentAction.RestartService,
                $"Restart the '{Publisher.Name}' service",
                DeploymentPlan.OutdatedBuild);
        }
    }

    public static IEnumerable<DeploymentStep> Remove(ObservedPublisher observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        bool installed = observed.Service.Installed;

        if (installed && observed.Service.State != ServiceRunState.Stopped)
        {
            yield return Step(
                DeploymentAction.StopService,
                $"Stop the '{Publisher.Name}' service",
                "nothing it holds is taken back while it runs");
        }

        // Even with the service already gone: the ACE names a computed SID, not a resolved name.
        if (observed.Encryption == NamespaceGrant.Granted)
        {
            yield return Step(
                DeploymentAction.RevokeEncryptionNamespaceAccess,
                $"Take back {Publisher.Account}'s access to BitLocker state",
                "the service is being removed");
        }

        if (!installed)
        {
            yield break;
        }

        // Unknown membership is tried too: 1377 means nothing to do, and a member left behind
        // stays in the group as an orphan SID once the service is deleted.
        if (observed.HyperVAdministrators is not null && observed.InHyperVAdministrators != false)
        {
            yield return Step(
                DeploymentAction.RemoveFromHyperVAdministrators,
                $"Remove {Publisher.Account} from '{observed.HyperVAdministrators}'",
                "the service is being removed",
                observed.HyperVAdministrators);
        }

        if (observed.LogsWritable)
        {
            yield return Step(
                DeploymentAction.RevokeLogsAccess,
                $"Revoke {Publisher.Account}'s access to its logs folder",
                "the folder and its logs stay: they are what explains the past");
        }

        if (observed.SnapshotWritable)
        {
            yield return Step(
                DeploymentAction.RevokeSnapshotAccess,
                $"Revoke {Publisher.Account}'s access to the snapshot folder",
                "the service is being removed");
        }

        if (observed.InstallFolderGranted)
        {
            yield return Step(
                DeploymentAction.RevokeConfigurationAccess,
                $"Revoke {Publisher.Account}'s access to the install folder",
                "the service is being removed");
        }

        yield return Step(
            DeploymentAction.RemoveService,
            $"Stop and delete the '{Publisher.Name}' service",
            "last, once nothing is left granted to its account");
    }

    private static DeploymentStep Step(
        DeploymentAction action, string description, string reason, string? target = null) =>
        new(Publisher, action, description, reason, target);

    private static bool SamePath(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
