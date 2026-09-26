using Ripcord.Domain;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Diagnostics;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Deployment;
using Ripcord.Ports.Diagnostics;
using Ripcord.Ports.Hosts;

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
    TimeSpan? SnapshotWrittenAgo = null,

    /// Null unless the service is running.
    RunningBuild? Build = null,

    /// What to paste into the two `ripcord.yaml` files, read without either.
    HostCertificates? Certificates = null,

    /// The publishing service, read like the listener: whatever else fails, it is shown.
    PublisherStatus? Publisher = null);

/// The publishing service as `ripcord service` shows it.
public sealed record PublisherStatus(
    ObservedService Service, string LogsFolder, LogReading? Log, ServiceVerdict Verdict, RunningBuild? Build);

/// Read-only: the service as Windows sees it, the deployment as the configuration wants it,
/// the end of the listener's log, and one line on why a stopped listener stopped.
public sealed class ServiceInspection(
    IConfigStore configStore,
    IDeploymentExecutor executor,
    IDiagnosticLogReader logReader,
    ICertificateProvider certificateStore)
{
    /// `thisBuild` is the build of the binary running this command, which is the one on disk.
    public ServiceReport Inspect(DeploymentRequest request, DateTimeOffset now, string thisBuild)
    {
        ArgumentNullException.ThrowIfNull(request);

        ObservedService service = ServiceReading.Read(executor);

        DeploymentOutcome deployment =
            new ListenerDeployment(configStore, executor).Plan(request, service);

        // Where the service writes, not where this command does: the listener logs beside
        // the binary Windows runs, whatever `--config` this run was given.
        string logsFolder = LogFolder.Beside(service.BinaryPath ?? request.BinaryPath);

        LogReading? log = null;

        foreach (string candidate in ServiceLog.Candidates(logsFolder, now))
        {
            if (logReader.Tail(candidate, ServiceLog.ReadLines) is { } reading)
            {
                log = reading;
                break;
            }
        }

        RunningBuild? build = ServiceProcessReading.Judge(
            logReader, service, logsFolder, thisBuild, request.BinaryPath);

        HostCertificates certificates = HostCertificateReading.Read(certificateStore, request.MachineName, now);

        PublisherStatus publisher = this.Publisher(request, deployment, now, thisBuild);

        return new ServiceReport(
            service,
            deployment,
            logsFolder,
            log,
            ServiceDiagnosis.Diagnose(
                service,
                LogsWritable(deployment, logsFolder),
                log,
                listenerDisabled: deployment.Desired is { ListenerEnabled: false },
                build),
            // A disabled listener serves nothing, so its snapshot's age means nothing here.
            deployment is { Desired: { ListenerEnabled: true } desired, Observed: { } observed }
                ? SnapshotFreshness.Judge(observed.SnapshotWrittenAt, now, desired.SnapshotStaleAfter)
                : null,
            now - deployment.Observed?.SnapshotWrittenAt,
            build,
            certificates,
            publisher);
    }

    private PublisherStatus Publisher(
        DeploymentRequest request, DeploymentOutcome deployment, DateTimeOffset now, string thisBuild)
    {
        RipcordService publisher = RipcordService.Publisher;
        ObservedService service = ServiceReading.Read(executor, publisher);
        string logsFolder = LogFolder.For(
            publisher.Origin, LogFolder.Beside(service.BinaryPath ?? request.BinaryPath));

        LogReading? log = ServiceLog.Candidates(logsFolder, now, publisher)
            .Select(candidate => logReader.Tail(candidate, ServiceLog.ReadLines))
            .FirstOrDefault(reading => reading is not null);

        return new PublisherStatus(
            service,
            logsFolder,
            log,
            ServiceDiagnosis.Diagnose(
                service,
                deployment.Observed?.Publisher?.LogsWritable,
                log,
                listenerDisabled: deployment.Desired is { ListenerEnabled: false },
                which: publisher),
            ServiceProcessReading.Judge(logReader, service, logsFolder, thisBuild, request.BinaryPath, publisher));
    }

    /// Only known for the folder the configuration's deployment looked at.
    private static bool? LogsWritable(DeploymentOutcome deployment, string logsFolder) =>
        deployment.Desired is { } desired
        && string.Equals(desired.LogsFolder, logsFolder, StringComparison.OrdinalIgnoreCase)
            ? deployment.Observed?.LogsWritableByService
            : null;
}
