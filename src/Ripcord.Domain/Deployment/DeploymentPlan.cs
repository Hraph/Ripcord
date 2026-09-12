namespace Ripcord.Domain.Deployment;

/// What the listener needs on this host, taken from the configuration.
public sealed record DesiredDeployment(
    string BinaryPath,
    string SnapshotPath,
    int Port,
    string PeerAddress);

/// What is on the host already. Filled in by the Windows adapter, which looks and reports;
/// it does not compare.
public sealed record ObservedDeployment(
    bool ServiceInstalled,
    string? ServiceBinaryPath,
    bool FirewallRuleInstalled,
    int? FirewallPort,
    string? FirewallRemoteAddress,
    bool SnapshotReadableByService)
{
    public static ObservedDeployment Nothing { get; } =
        new(false, null, false, null, null, false);
}

public enum DeploymentAction
{
    CreateService,
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

    /// Re-running a correct deployment yields an empty plan: an installer that reinstalls
    /// every time is one nobody dares run twice. A wrong binary path, port or peer address is
    /// an update rather than a teardown, which is what makes this a migration too.
    public static DeploymentPlan For(DesiredDeployment desired, ObservedDeployment observed)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(observed);

        List<DeploymentStep> steps = [];

        // Order matters: the service exists before the port is opened, so the port is never
        // open onto nothing.
        if (!observed.ServiceInstalled)
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.CreateService,
                $"Create the '{ServiceName}' service running '{desired.BinaryPath} serve' "
                + $"as {ServiceAccount}",
                "no service is installed on this host"));
        }
        else if (!SamePath(observed.ServiceBinaryPath, desired.BinaryPath))
        {
            steps.Add(new DeploymentStep(
                DeploymentAction.UpdateService,
                $"Point the '{ServiceName}' service at '{desired.BinaryPath} serve'",
                $"it currently runs '{observed.ServiceBinaryPath}'"));
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
                $"Grant {ServiceAccount} read access to '{desired.SnapshotPath}'",
                "the service account cannot read the snapshot it is meant to serve"));
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
                $"Revoke {ServiceAccount}'s access to the snapshot file",
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
