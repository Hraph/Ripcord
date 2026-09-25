using Ripcord.Domain.Configuration;

namespace Ripcord.Domain.Deployment;

/// Why the other host cannot read this one. `Critical` when the configuration wants a
/// listener and Windows says none is running; not when that cannot be told.
public sealed record ListenerAlert(string Headline, string Reason, string Next, bool Critical);

/// A listener the other host can read, said rather than left to be inferred from silence.
/// `Build` is null when the running process recorded none.
public sealed record ListenerRunning(bool Starting, string? Build, bool Outdated);

/// The line at the bottom of `ripcord status`. Over there this host shows SILENT, which reads
/// as a network fault; the cause is often here, and only this side can see it.
public static class ListenerAvailability
{
    public static ListenerAlert? Judge(
        ListenerSettings listener, string peerName, ObservedService service)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(service);

        // A choice, and the PEER section already says the channel is off on this node.
        if (!listener.Enabled)
        {
            return null;
        }

        if (!service.Installed)
        {
            return new ListenerAlert(
                "NOT INSTALLED",
                $"no listener service on this host, so {peerName} cannot read it",
                "ripcord service install --dry-run",
                Critical: true);
        }

        return service.State switch
        {
            ServiceRunState.Running or ServiceRunState.StartPending => null,
            ServiceRunState.Unknown => new ListenerAlert(
                "UNKNOWN",
                ServiceDiagnosis.Undescribed(service.Unreadable),
                "ripcord service",
                Critical: false),
            _ => new ListenerAlert(
                "NOT RUNNING",
                $"the listener service is {Word(service.State)}, so {peerName} cannot read "
                    + "this host",
                "ripcord service",
                Critical: true),
        };
    }

    /// The other side of `Judge`: exactly when it has nothing to report and the listener is on.
    public static ListenerRunning? Running(
        ListenerSettings listener, ObservedService service, RunningBuild? build)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(service);

        return listener.Enabled
            && service.Installed
            && service.State is ServiceRunState.Running or ServiceRunState.StartPending
            ? new ListenerRunning(
                service.State == ServiceRunState.StartPending, build?.Build, build?.Outdated == true)
            : null;
    }

    private static string Word(ServiceRunState state) => state switch
    {
        ServiceRunState.Paused => "paused",
        ServiceRunState.StopPending => "stopping",
        _ => "stopped",
    };
}
