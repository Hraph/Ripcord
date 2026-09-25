namespace Ripcord.Domain.Deployment;

public enum ServiceChange
{
    Restart,
    Start,
    Stop,
}

/// What `service restart`, `start` or `stop` would do, from the state Windows reports.
///
/// Exactly one of three: a plan, a sentence saying there is nothing to do, or the reason the
/// state could not be read. An unreadable state is never taken for a stopped one.
public sealed record ServiceStateChange(DeploymentPlan Plan, string? Unchanged, string? Unreadable)
{
    public static ServiceStateChange For(
        ServiceChange change, ObservedService service, ObservedDeployment observed)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(observed);

        if (change == ServiceChange.Restart)
        {
            return Planned(DeploymentPlan.ToRestart(observed));
        }

        if (service.State == ServiceRunState.Unknown)
        {
            return new ServiceStateChange(
                new DeploymentPlan([]), null, ServiceDiagnosis.Undescribed(service.Unreadable));
        }

        return (change, service.State) switch
        {
            (ServiceChange.Start, ServiceRunState.Stopped) =>
                Planned(DeploymentPlan.ToRestart(observed with { ServiceRunning = false })),
            (ServiceChange.Start, ServiceRunState.Running) => Said("is already running"),
            (ServiceChange.Start, ServiceRunState.StartPending) =>
                Said("is already starting: look again in a few seconds"),
            (ServiceChange.Start, ServiceRunState.StopPending) =>
                Said("is stopping: look again in a few seconds, then start it"),
            (ServiceChange.Start, _) =>
                Said("is paused, which Ripcord never does itself: run 'ripcord service restart'"),

            (ServiceChange.Stop, ServiceRunState.Stopped) => Said("is not running"),
            (ServiceChange.Stop, ServiceRunState.StopPending) =>
                Said("is already stopping: look again in a few seconds"),
            _ => Planned(DeploymentPlan.ToStop(observed with { ServiceRunning = true })),
        };
    }

    private static ServiceStateChange Planned(DeploymentPlan plan) => new(plan, null, null);

    private static ServiceStateChange Said(string what) =>
        new(new DeploymentPlan([]), $"The '{DeploymentPlan.ServiceName}' service {what}. Nothing was changed.", null);
}
