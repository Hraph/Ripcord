using Ripcord.Domain.Configuration;

namespace Ripcord.Domain.Deployment;

/// What the listener needs on this host, taken from the configuration.
public sealed record DesiredDeployment(
    string BinaryPath,
    string SnapshotPath,
    int Port,
    string PeerAddress,

    /// The only folder the service account may write to: the listener's log goes there.
    string LogsFolder,

    /// `listener.enabled`. Off, there is nothing to install, but a service installed earlier
    /// can still be looked at and removed.
    bool ListenerEnabled = true,

    /// `peer.offline_after_sec`: a snapshot older than this is one the peer reads as stale.
    TimeSpan? SnapshotStaleAfter = null,

    /// `listener.local_certificate_thumbprint`: the certificate whose private key the
    /// listener signs its handshake with.
    string? CertificateThumbprint = null,

    /// `storage.check_bitlocker_autounlock`: only then may the publisher read BitLocker.
    bool CheckBitLocker = false)
{
    /// Access is granted on the folder, never on the snapshot file. `ripcord` rewrites the
    /// snapshot by moving a temporary file over it, and a move brings the new file's access
    /// list with it — an entry granted on the file itself would survive exactly one write.
    /// An inheritable entry on the folder covers whatever lands in it, including the file
    /// that does not exist yet on a host being deployed for the first time.
    ///
    /// Never empty for a deployment that exists: the validator refuses an enabled listener
    /// whose snapshot path names no folder, and a disabled one is never deployed at all.
    ///
    /// It used to fall back to the snapshot path itself, which read as harmless and was not —
    /// the executor creates this folder, so a bare `state.json` had a *directory* created
    /// exactly where the snapshot file has to be written. Every later publish then failed, and
    /// failed quietly, because the one caller catches that.
    public string SnapshotFolder => WindowsPath.FolderOf(SnapshotPath);

    /// Where the binary is, and so `ripcord.yaml`: services run with no `--config`.
    public string InstallFolder => WindowsPath.FolderOf(BinaryPath);

    public string PublisherLogsFolder =>
        Diagnostics.LogFolder.For(Diagnostics.DiagnosticOrigin.Publisher, LogsFolder);
}

/// What is on the host already. Filled in by the Windows adapter, which looks and reports;
/// it does not compare.
public sealed record ObservedDeployment(
    bool ServiceInstalled,
    string? ServiceBinaryPath,
    bool FirewallRuleInstalled,
    int? FirewallPort,
    string? FirewallRemoteAddress,
    bool SnapshotReadableByService,

    /// Whether the service is running, as distinct from installed. A listener that is
    /// registered and stopped serves nothing, and the pair view on the other host degrades to
    /// "offline" — which reads as a network problem rather than as a service somebody has to
    /// start.
    bool ServiceRunning = false,
    bool LogsWritableByService = false,

    /// The Application event log source the listener reports a failed start under. Without it
    /// registered, a virtual account cannot write there at all.
    bool EventSourceRegistered = false,

    /// When the snapshot was last written; null when there is no file.
    DateTimeOffset? SnapshotWrittenAt = null,

    /// Whether the service account can read the certificate's private key. Null when there is
    /// no such key to look at: no thumbprint, no certificate, or no key file found.
    bool? KeyReadableByService = null,

    /// The configured certificate's key file, when found.
    string? KeyFile = null,

    /// Every machine key file giving the service account an entry of its own.
    IReadOnlyList<string>? KeyFilesGranted = null,

    /// Those of `StaleFolderCandidates` giving the service account an entry of its own.
    IReadOnlyList<string>? FoldersGranted = null,

    /// Whether the service account can read `ripcord.yaml`, inherited entries included: it
    /// reads it at every start, and nothing else guarantees it once the snapshot moved.
    bool ConfigurationReadableByService = false,

    /// Whether the account has an entry of its own on the install folder: only that one is
    /// Ripcord's to take back, whatever else lets it read there.
    bool InstallFolderGrantedToService = false,

    /// The publishing service and what it was granted; null reads as none of it there.
    ObservedPublisher? Publisher = null)
{
    public static ObservedDeployment Nothing { get; } =
        new(false, null, false, null, null, false);

    public ObservedPublisher PublisherOrNothing => this.Publisher ?? ObservedPublisher.Nothing;
}

public enum DeploymentAction
{
    CreateService,

    /// Stop, then start. The listener reads `ripcord.yaml` once, when it starts, so an edited
    /// configuration changes nothing until this has run — and the operator who just edited the
    /// file is the one who needs it, not a deployment.
    RestartService,

    /// Separate from creating it. `sc create` and `sc start` are two things that can fail
    /// independently, and a step that does both reports neither: a create that succeeded
    /// followed by a start that did not would be reported as nothing having been done, on a
    /// host that now holds a registered service.
    StartService,
    UpdateService,
    RemoveService,
    CreateFirewallRule,
    UpdateFirewallRule,
    RemoveFirewallRule,
    GrantSnapshotAccess,
    RevokeSnapshotAccess,
    GrantLogsAccess,
    RevokeLogsAccess,
    RegisterEventSource,
    RemoveEventSource,
    GrantKeyAccess,
    RevokeKeyAccess,

    /// Read on the files of the install folder, `ripcord.yaml` among them, and not below it.
    GrantConfigurationAccess,
    RevokeConfigurationAccess,

    /// Leaves the service installed. Until it starts again, the other host cannot read this one.
    StopService,

    /// Access the service account keeps to something the configuration no longer uses: the key
    /// of a certificate since replaced, a folder since moved. The path is the step's `Target`.
    RevokeStaleKeyAccess,
    RevokeStaleFolderAccess,

    /// Modify on the snapshot folder, for the publishing service alone.
    GrantSnapshotWriteAccess,

    /// The group is the step's `Target`, by the name this host gives it.
    AddToHyperVAdministrators,
    RemoveFromHyperVAdministrators,

    /// One ACE on the BitLocker WMI namespace, for the publishing service alone.
    GrantEncryptionNamespaceAccess,
    RevokeEncryptionNamespaceAccess,
}

/// One change, and why it is needed. The reason is what `--dry-run` prints, so it is written
/// for the operator rather than for the log.
public sealed record DeploymentStep(
    DeploymentAction Action,
    string Description,
    string Reason,

    /// What a stale revoke acts on; the other steps act on the configuration's own paths.
    string? Target = null,

    /// Whether the revoke reaches into the folder's contents. Never for a folder that holds the
    /// logs or the snapshot still in use: `/t` would strip their grants too.
    bool Recursive = false,

    /// Which service the step is about; null is the listener.
    RipcordService? Service = null)
{
    public RipcordService Subject => this.Service ?? RipcordService.Listener;
}

/// The difference between what is on the host and what should be. Deployment and removal are
/// the same list read in two directions, so an uninstaller cannot drift from its installer.
/// `BlockedBy` is set when the configuration cannot be deployed on this host at all: the plan
/// then holds no step, so nothing is half-applied.
public sealed record DeploymentPlan(IReadOnlyList<DeploymentStep> Steps, string? BlockedBy = null)
{
    private static readonly string ServiceName = RipcordService.Listener.Name;

    private static readonly string ServiceAccount = RipcordService.Listener.Account;

    public const string FirewallRuleName = "Ripcord listener";

    public const string EventSource = "ripcord";

    public bool ChangesAnything => Steps.Count > 0;

    public bool IsBlocked => BlockedBy is not null;

    /// Putting an edited configuration into effect, which is not a deployment: nothing about
    /// the host changes, the listener simply reads the file again.
    ///
    /// A service that is not running is started rather than restarted — `sc stop` on a stopped
    /// service is an error, and an operator asking for the configuration to take effect means
    /// the same thing either way.
    public static DeploymentPlan ToRestart(ObservedDeployment observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        if (!observed.ServiceInstalled)
        {
            return new DeploymentPlan([]);
        }

        return new DeploymentPlan(
        [
            observed.ServiceRunning
                ? new DeploymentStep(
                    DeploymentAction.RestartService,
                    $"Restart the '{ServiceName}' service",
                    "the listener reads the configuration once, when it starts")
                : new DeploymentStep(
                    DeploymentAction.StartService,
                    $"Start the '{ServiceName}' service",
                    "it is installed and not running"),
        ]);
    }

    /// Nothing to stop on a host with no service, or one already stopped: the plan is empty.
    public static DeploymentPlan ToStop(ObservedDeployment observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        return observed is { ServiceInstalled: true, ServiceRunning: true }
            ? new DeploymentPlan(
            [
                new DeploymentStep(
                    DeploymentAction.StopService,
                    $"Stop the '{ServiceName}' service",
                    "the service stays installed; 'ripcord service start' brings it back"),
            ])
            : new DeploymentPlan([]);
    }

    /// Re-running a correct deployment yields an empty plan: an installer that reinstalls
    /// every time is one nobody dares run twice. A wrong binary path, port or peer address is
    /// an update rather than a teardown, which is what makes this a migration too.
    public static DeploymentPlan For(DesiredDeployment desired, ObservedDeployment observed)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(observed);

        if (!desired.ListenerEnabled)
        {
            return new DeploymentPlan([], ListenerDisabled);
        }

        ObservedPublisher publisher = observed.PublisherOrNothing;

        if (publisher.HyperVAdministrators is null)
        {
            return new DeploymentPlan([], PublisherSteps.NoHyperVAdministrators);
        }

        List<DeploymentStep> steps = [];

        // Set when the service will need starting, and acted on last — see below.
        string? start = null;
        DeploymentStep? update = null;

        // Order matters: the service exists before the port is opened, so the port is never
        // open onto nothing.
        if (!observed.ServiceInstalled)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.CreateService,
                $"Create the '{ServiceName}' service running '{desired.BinaryPath} serve' "
                + $"as {ServiceAccount}",
                "no service is installed on this host"));

            start = "a service that is registered and stopped serves nothing";
        }
        else if (!SamePath(observed.ServiceBinaryPath, desired.BinaryPath))
        {
            // Applied last, like a start: it restarts the service, which needs its folders.
            update = new DeploymentStep(
                DeploymentAction.UpdateService,
                $"Point the '{ServiceName}' service at '{desired.BinaryPath} serve'",
                $"it currently runs '{observed.ServiceBinaryPath}'");
        }
        else if (!observed.ServiceRunning)
        {
            // The case a plan that only compared paths could not see: installed, correct, and
            // stopped. Re-running the command is then what starts it, which is what an
            // operator expects of a command that reconciles.
            start = "it is installed and not running";
        }

        // Created before any grant: its account only exists once the service does.
        steps.AddRange(PublisherSteps.Create(desired, publisher));

        if (!observed.FirewallRuleInstalled)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.CreateFirewallRule,
                $"Allow inbound TCP {desired.Port} from {desired.PeerAddress} only",
                "no firewall rule restricts the listener to the peer"));
        }
        else if (observed.FirewallPort != desired.Port
            || !SamePath(observed.FirewallRemoteAddress, desired.PeerAddress))
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.UpdateFirewallRule,
                $"Allow inbound TCP {desired.Port} from {desired.PeerAddress} only",
                $"it currently allows port {observed.FirewallPort} "
                + $"from {observed.FirewallRemoteAddress}"));
        }

        if (!observed.ConfigurationReadableByService)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.GrantConfigurationAccess,
                $"Grant {ServiceAccount} read access to the files in '{desired.InstallFolder}'",
                "it reads ripcord.yaml there at every start; nothing below it, nothing written"));
        }

        if (!observed.SnapshotReadableByService)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.GrantSnapshotAccess,
                $"Grant {ServiceAccount} read access to '{desired.SnapshotFolder}'",
                $"that folder holds {SnapshotFileName(desired)}, "
                + ListenerSettings.SnapshotMeaning));
        }

        if (!observed.LogsWritableByService)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.GrantLogsAccess,
                $"Create '{desired.LogsFolder}' and grant {ServiceAccount} modify access to it",
                "the listener writes its log there; it may write nowhere else"));
        }

        // Machine keys are readable by SYSTEM and Administrators only, and a virtual account is
        // neither: without this the handshake fails on this host's own key.
        if (desired.CertificateThumbprint is { } thumbprint && observed.KeyReadableByService == false)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.GrantKeyAccess,
                $"Grant {ServiceAccount} read access to the private key of certificate "
                + thumbprint,
                "the listener proves this host's identity with it"));
        }

        if (!observed.EventSourceRegistered)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.RegisterEventSource,
                $"Register the '{EventSource}' source in the Application event log",
                "both services report there a start they cannot log to their file"));
        }

        steps.AddRange(PublisherSteps.Grants(desired, publisher));

        // Started last, after the rule that lets the peer in, the access it needs to the file
        // it serves and the folder it logs to. Starting it first would bring up a listener that
        // cannot read its own snapshot, and the peer would be told this host has nothing to say
        // rather than that it is half-deployed.
        if (update is not null)
        {
            steps.Add(update);
        }

        if (start is not null)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.StartService, $"Start the '{ServiceName}' service", start));
        }

        // After the listener: a publisher that fails to start still leaves something served.
        steps.AddRange(PublisherSteps.Starts(desired, publisher));

        // Last: the listener runs on what it needs before anything it no longer needs is taken.
        steps.AddRange(Stale(desired, observed));

        return new DeploymentPlan(steps);
    }

    /// Folders an earlier deployment granted and this one does not use: the old binary's folder
    /// and its `logs`, once the service moved. Never the current install folder, which the
    /// service may need to read `ripcord.yaml` (V55).
    public static IReadOnlyList<string> StaleFolderCandidates(
        DesiredDeployment desired, string? serviceBinaryPath)
    {
        ArgumentNullException.ThrowIfNull(desired);

        List<string> candidates = [];

        if (serviceBinaryPath is { Length: > 0 } running && !SamePath(running, desired.BinaryPath))
        {
            string old = WindowsPath.FolderOf(running);
            candidates.Add(old);
            candidates.Add(WindowsPath.Join(old, "logs"));
        }

        return [.. candidates
            .Where(folder => folder.Length > 0
                && !SamePath(folder, WindowsPath.FolderOf(desired.BinaryPath))
                && !SamePath(folder, desired.SnapshotFolder)
                && !SamePath(folder, desired.LogsFolder))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static IEnumerable<DeploymentStep> Stale(DesiredDeployment desired, ObservedDeployment observed)
    {
        // Without the current key found, which one is current cannot be told: none is taken.
        foreach (string key in observed.KeyFile is null ? [] : observed.KeyFilesGranted ?? [])
        {
            if (!SamePath(key, observed.KeyFile))
            {
                // The name alone fits on a line at 75 columns; the full path would be cut in two.
                string folder = WindowsPath.FolderOf(key);
                string name = key[folder.Length..].TrimStart('\\');

                yield return new DeploymentStep(
                    DeploymentAction.RevokeStaleKeyAccess,
                    $"Remove {ServiceAccount}'s access to key file {name}",
                    $"it is in {folder}, and no configured certificate uses it any more",
                    key);
            }
        }

        foreach (string folder in observed.FoldersGranted ?? [])
        {
            yield return new DeploymentStep(
                DeploymentAction.RevokeStaleFolderAccess,
                $"Remove {ServiceAccount}'s access to '{folder}'",
                "the configuration no longer uses it",
                folder,
                !Holds(folder, desired.LogsFolder)
                    && !Holds(folder, desired.SnapshotFolder)
                    && !Holds(folder, WindowsPath.FolderOf(desired.BinaryPath)));
        }
    }

    private static bool Holds(string folder, string path) =>
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// The same steps in reverse: the port closes before the service goes, so the port is
    /// never open onto nothing here either. The snapshot file stays — it holds no secret, and
    /// an uninstaller that deletes data is one people are afraid to run.
    public static DeploymentPlan ToRemove(ObservedDeployment observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        // The publisher first, and all of it while its service still exists: the account's
        // name only resolves as long as it does.
        List<DeploymentStep> steps = [.. PublisherSteps.Remove(observed.PublisherOrNothing)];

        if (observed.EventSourceRegistered)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.RemoveEventSource,
                $"Remove the '{EventSource}' source from the Application event log",
                "nothing will report under it any more"));
        }

        if (observed.KeyReadableByService == true)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.RevokeKeyAccess,
                $"Revoke {ServiceAccount}'s access to the certificate's private key",
                "the service account no longer needs it"));
        }

        // The folder and its logs stay, like the snapshot: they are what explains the past.
        if (observed.LogsWritableByService)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.RevokeLogsAccess,
                $"Revoke {ServiceAccount}'s access to the logs folder",
                "the service account no longer needs it"));
        }

        if (observed.SnapshotReadableByService)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.RevokeSnapshotAccess,
                $"Revoke {ServiceAccount}'s access to the snapshot folder",
                "the service account no longer needs it"));
        }

        if (observed.InstallFolderGrantedToService)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.RevokeConfigurationAccess,
                $"Revoke {ServiceAccount}'s access to the install folder",
                "the service account no longer needs to read ripcord.yaml"));
        }

        if (observed.FirewallRuleInstalled)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.RemoveFirewallRule,
                $"Remove the '{FirewallRuleName}' firewall rule",
                $"it currently allows inbound TCP {observed.FirewallPort}"));
        }

        if (observed.ServiceInstalled)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.RemoveService,
                $"Stop and delete the '{ServiceName}' service",
                $"it currently runs '{observed.ServiceBinaryPath}'"));
        }

        return new DeploymentPlan(steps);
    }

    /// The steps after which the service is expected to be running, and so the ones whose
    /// failure is a service that did not start rather than a command that did not run.
    public static bool StartsTheService(DeploymentAction action) =>
        action is DeploymentAction.StartService
            or DeploymentAction.RestartService
            or DeploymentAction.UpdateService;

    public const string ListenerDisabled =
        "the listener is disabled in ripcord.yaml (listener.enabled: false). Set it to true "
        + "to install the listener, or run 'ripcord service remove' to take it off this host.";

    private static string SnapshotFileName(DesiredDeployment desired)
    {
        string folder = desired.SnapshotFolder;
        return folder.Length == 0 ? desired.SnapshotPath : desired.SnapshotPath[folder.Length..]
            .TrimStart('\\', '/');
    }

    /// Windows paths and addresses are case-insensitive; a difference in case is not a change.
    private static bool SamePath(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
