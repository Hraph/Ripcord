namespace Ripcord.Domain.Deployment;

public enum ServiceChange
{
    Restart,
    Start,
    Stop,
}

/// What `service restart`, `start` or `stop` would do, from the state Windows reports.
///
/// A plan, sentences saying what there is nothing to do about, or the reason a state could not
/// be read — in which case nothing is planned: an unreadable state is never taken for a
/// stopped one.
public sealed record ServiceStateChange(DeploymentPlan Plan, string? Unchanged, string? Unreadable)
{
    /// The listener alone.
    public static ServiceStateChange For(ServiceChange change, ObservedService service) =>
        Of(change, service, RipcordService.Listener);

    /// Both services: started listener first, so the peer is served before it is refreshed;
    /// stopped publisher first, so nothing is written for a listener that no longer serves.
    public static ServiceStateChange ForBoth(
        ServiceChange change, ObservedService listener, ObservedService publisher)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(publisher);

        ServiceStateChange first = Of(change, listener, RipcordService.Listener);
        ServiceStateChange second = Of(change, publisher, RipcordService.Publisher);

        if (change == ServiceChange.Stop)
        {
            (first, second) = (second, first);
        }

        if ((first.Unreadable ?? second.Unreadable) is { } unreadable)
        {
            return new ServiceStateChange(new DeploymentPlan([]), null, unreadable);
        }

        string[] unchanged = [.. new[] { first.Unchanged, second.Unchanged }.OfType<string>()];

        return new ServiceStateChange(
            new DeploymentPlan([.. first.Plan.Steps, .. second.Plan.Steps]),
            unchanged.Length == 0 ? null : string.Join('\n', unchanged),
            null);
    }

    private static ServiceStateChange Of(ServiceChange change, ObservedService service, RipcordService which)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (!service.Installed)
        {
            return Said(which, "is not installed: run 'ripcord service install'");
        }

        // A restart is what follows an edit of ripcord.yaml: whatever the state, it ends running.
        if (change == ServiceChange.Restart)
        {
            return service.State == ServiceRunState.Running
                ? Planned(which, DeploymentAction.RestartService, "Restart",
                    $"the {which.Role} reads the configuration once, when it starts")
                : Planned(which, DeploymentAction.StartService, "Start", "it is installed and not running");
        }

        if (service.State == ServiceRunState.Unknown)
        {
            return new ServiceStateChange(
                new DeploymentPlan([]), null, ServiceDiagnosis.Undescribed(service.Unreadable));
        }

        return (change, service.State) switch
        {
            (ServiceChange.Start, ServiceRunState.Stopped) =>
                Planned(which, DeploymentAction.StartService, "Start", "it is installed and not running"),
            (ServiceChange.Start, ServiceRunState.Running) => Said(which, "is already running"),
            (ServiceChange.Start, ServiceRunState.StartPending) =>
                Said(which, "is already starting: look again in a few seconds"),
            (ServiceChange.Start, ServiceRunState.StopPending) =>
                Said(which, "is stopping: look again in a few seconds, then start it"),
            (ServiceChange.Start, _) =>
                Said(which, "is paused, which Ripcord never does itself: run 'ripcord service restart'"),

            (ServiceChange.Stop, ServiceRunState.Stopped) => Said(which, "is not running"),
            (ServiceChange.Stop, ServiceRunState.StopPending) =>
                Said(which, "is already stopping: look again in a few seconds"),
            _ => Planned(which, DeploymentAction.StopService, "Stop",
                "the service stays installed; 'ripcord service start' brings it back"),
        };
    }

    private static ServiceStateChange Planned(
        RipcordService which, DeploymentAction action, string verb, string reason) =>
        new(
            new DeploymentPlan(
            [
                new DeploymentStep(
                    action,
                    $"{verb} the '{which.Name}' service",
                    reason,
                    Service: which == RipcordService.Listener ? null : which),
            ]),
            null,
            null);

    private static ServiceStateChange Said(RipcordService which, string what) =>
        new(new DeploymentPlan([]), $"The '{which.Name}' service {what}. Nothing was changed.", null);
}
