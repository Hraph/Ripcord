namespace Ripcord.Domain.Deployment;

/// What the listener needs on this host, taken from the configuration.
public sealed record DesiredDeployment(
    string BinaryPath,
    string SnapshotPath,
    int Port,
    string PeerAddress)
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
    bool ServiceRunning = false)
{
    public static ObservedDeployment Nothing { get; } =
        new(false, null, false, null, null, false);
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
}

/// One change, and why it is needed. The reason is what `--dry-run` prints, so it is written
/// for the operator rather than for the log.
public sealed record DeploymentStep(
    DeploymentAction Action, string Description, string Reason);

/// The difference between what is on the host and what should be. Deployment and removal are
/// the same list read in two directions, so an uninstaller cannot drift from its installer.
public sealed record DeploymentPlan(IReadOnlyList<DeploymentStep> Steps)
{
    public const string ServiceName = "ripcord";

    public const string ServiceAccount = @"NT SERVICE\ripcord";

    public const string FirewallRuleName = "Ripcord listener";

    public bool ChangesAnything => Steps.Count > 0;

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

    /// Re-running a correct deployment yields an empty plan: an installer that reinstalls
    /// every time is one nobody dares run twice. A wrong binary path, port or peer address is
    /// an update rather than a teardown, which is what makes this a migration too.
    public static DeploymentPlan For(DesiredDeployment desired, ObservedDeployment observed)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(observed);

        List<DeploymentStep> steps = [];

        // Set when the service will need starting, and acted on last — see below.
        string? start = null;

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
            steps.Add(new DeploymentStep(
                DeploymentAction.UpdateService,
                $"Point the '{ServiceName}' service at '{desired.BinaryPath} serve'",
                $"it currently runs '{observed.ServiceBinaryPath}'"));
        }
        else if (!observed.ServiceRunning)
        {
            // The case a plan that only compared paths could not see: installed, correct, and
            // stopped. Re-running the command is then what starts it, which is what an
            // operator expects of a command that reconciles.
            start = "it is installed and not running";
        }

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

        if (!observed.SnapshotReadableByService)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.GrantSnapshotAccess,
                $"Grant {ServiceAccount} read access to '{desired.SnapshotFolder}'",
                "the service account cannot read the folder holding the snapshot it serves"));
        }

        // Started last, after the rule that lets the peer in and the access it needs to the
        // file it serves. Starting it first would bring up a listener that cannot read its own
        // snapshot, and the peer would be told this host has nothing to say rather than that
        // it is half-deployed.
        if (start is not null)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.StartService, $"Start the '{ServiceName}' service", start));
        }

        return new DeploymentPlan(steps);
    }

    /// The same steps in reverse: the port closes before the service goes, so the port is
    /// never open onto nothing here either. The snapshot file stays — it holds no secret, and
    /// an uninstaller that deletes data is one people are afraid to run.
    public static DeploymentPlan ToRemove(ObservedDeployment observed)
    {
        ArgumentNullException.ThrowIfNull(observed);

        List<DeploymentStep> steps = [];

        if (observed.SnapshotReadableByService)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.RevokeSnapshotAccess,
                $"Revoke {ServiceAccount}'s access to the snapshot folder",
                "the service account no longer needs it"));
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

    /// Windows paths and addresses are case-insensitive; a difference in case is not a change.
    private static bool SamePath(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
