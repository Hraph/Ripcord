using Ripcord.Domain;
using Ripcord.Domain.Deployment;
using Ripcord.Ports.Diagnostics;

namespace Ripcord.Application.Deployment;

/// The build the running listener recorded, for `ripcord service` and `ripcord status` alike.
public static class ServiceProcessReading
{
    /// Read only for a running service: a stopped one has no build to report.
    public static RunningBuild? Judge(
        IDiagnosticLogReader logReader,
        ObservedService service,
        string logsFolder,
        string thisBuild,
        string thisBinary,
        RipcordService recording)
    {
        ArgumentNullException.ThrowIfNull(logReader);
        ArgumentNullException.ThrowIfNull(service);

        // A record left by an earlier run is rejected by its process id, not here.
        ServiceProcess? recorded =
            service.State == ServiceRunState.Running
            && recording.ProcessRecords(logsFolder)
                .Select(path => logReader.Tail(path, 4))
                .FirstOrDefault(record => record is { Unreadable: null }) is { } record
                ? ServiceProcess.Parse(record.Lines)
                : null;

        return RunningBuild.Judge(service, recorded, thisBuild, thisBinary);
    }
}
