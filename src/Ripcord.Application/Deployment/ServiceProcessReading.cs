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

        ServiceProcess? recorded =
            service.State == ServiceRunState.Running
            && logReader.Tail(WindowsPath.Join(logsFolder, recording.ProcessFile), 4) is
                { Unreadable: null } record
                ? ServiceProcess.Parse(record.Lines)
                : null;

        return RunningBuild.Judge(service, recorded, thisBuild, thisBinary);
    }
}
