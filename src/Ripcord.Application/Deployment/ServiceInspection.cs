using Ripcord.Domain.Deployment;
using Ripcord.Domain.Diagnostics;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Deployment;
using Ripcord.Ports.Diagnostics;

namespace Ripcord.Application.Deployment;

/// Everything `ripcord service` shows. `Deployment` holds the configuration's errors when
/// `ripcord.yaml` does not load; the service, its log and the verdict are there regardless.
public sealed record ServiceReport(
    ObservedService Service,
    DeploymentOutcome Deployment,
    string LogsFolder,
    LogReading? Log,
    ServiceVerdict Verdict,

    /// Null when the configuration did not load or the host could not be read.
    SnapshotAge? Snapshot = null,
    TimeSpan? SnapshotWrittenAgo = null);

/// Read-only: the service as Windows sees it, the deployment as the configuration wants it,
/// the end of the listener's log, and one line on why a stopped listener stopped.
public sealed class ServiceInspection(
    IConfigStore configStore, IDeploymentExecutor executor, IDiagnosticLogReader logReader)
{
    public ServiceReport Inspect(DeploymentRequest request, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(request);

        ObservedService service;

        try
        {
            service = executor.ObserveService();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            service = new ObservedService(
                true, null, ServiceRunState.Unknown, null, null, null, exception.Message);
        }

        DeploymentOutcome deployment =
            new ListenerDeployment(configStore, executor).Plan(request, service);

        // Where the service writes, not where this command does: the listener logs beside
        // the binary Windows runs, whatever `--config` this run was given.
        string logsFolder = LogFolder.Beside(service.BinaryPath ?? request.BinaryPath);

        LogReading? log = null;

        foreach (string candidate in ListenerLog.Candidates(logsFolder, now))
        {
            if (logReader.Tail(candidate, ListenerLog.ReadLines) is { } reading)
            {
                log = reading;
                break;
            }
        }

        return new ServiceReport(
            service,
            deployment,
            logsFolder,
            log,
            ServiceDiagnosis.Diagnose(
                service,
                LogsWritable(deployment, logsFolder),
                log,
                listenerDisabled: deployment.Desired is { ListenerEnabled: false }),
            deployment is { Desired: { } desired, Observed: { } observed }
                ? SnapshotFreshness.Judge(observed.SnapshotWrittenAt, now, desired.SnapshotStaleAfter)
                : null,
            now - deployment.Observed?.SnapshotWrittenAt);
    }

    /// Only known for the folder the configuration's deployment looked at.
    private static bool? LogsWritable(DeploymentOutcome deployment, string logsFolder) =>
        deployment.Desired is { } desired
        && string.Equals(desired.LogsFolder, logsFolder, StringComparison.OrdinalIgnoreCase)
            ? deployment.Observed?.LogsWritableByService
            : null;
}
