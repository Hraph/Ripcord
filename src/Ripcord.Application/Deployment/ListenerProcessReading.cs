using Ripcord.Domain;
using Ripcord.Domain.Deployment;
using Ripcord.Ports.Diagnostics;

namespace Ripcord.Application.Deployment;

/// The build the running listener recorded, for `ripcord service` and `ripcord status` alike.
public static class ListenerProcessReading
{
    /// Read only for a running service: a stopped one has no build to report.
    public static RunningBuild? Judge(
        IDiagnosticLogReader logReader,
        ObservedService service,
        string logsFolder,
        string thisBuild,
        string thisBinary)
    {
        ArgumentNullException.ThrowIfNull(logReader);
        ArgumentNullException.ThrowIfNull(service);

        ListenerProcess? recorded =
            service.State == ServiceRunState.Running
            && logReader.Tail(WindowsPath.Join(logsFolder, ListenerProcess.FileName), 4) is
                { Unreadable: null } record
                ? ListenerProcess.Parse(record.Lines)
                : null;

        return RunningBuild.Judge(service, recorded, thisBuild, thisBinary);
    }
}
