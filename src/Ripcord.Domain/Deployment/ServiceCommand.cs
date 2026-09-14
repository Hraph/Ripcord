namespace Ripcord.Domain.Deployment;

/// What a service command's exit code means.
///
/// `sc.exe` reports "it is already like that" as a failure: starting a service that is running
/// exits 1056, stopping one that is stopped exits 1062. Both are the state the step was asking
/// for, and reading them as failures turns a redundant step into a deployment that reports
/// itself broken — which is what a transient failure to read the running state would otherwise
/// produce on a host where nothing at all is wrong.
///
/// Here rather than in the adapter because it is a judgement about what an outcome means, and
/// because the adapter cannot be executed anywhere a test can reach it.
public static class ServiceCommand
{
    /// ERROR_SERVICE_ALREADY_RUNNING.
    public const int AlreadyRunning = 1056;

    /// ERROR_SERVICE_NOT_ACTIVE.
    public const int NotRunning = 1062;

    /// Whether this code says the service is already in the state the action wanted.
    ///
    /// Scoped to the actions that start and stop. A create, a delete or an ACL change that
    /// reports one of these numbers is reporting something else, and must not be waved
    /// through by a coincidence of numbering.
    public static bool LeavesNothingToDo(DeploymentAction action, int exitCode) =>
        action switch
        {
            DeploymentAction.StartService => exitCode == AlreadyRunning,
            DeploymentAction.RestartService => exitCode is AlreadyRunning or NotRunning,
            DeploymentAction.RemoveService => exitCode == NotRunning,
            _ => false,
        };
}
