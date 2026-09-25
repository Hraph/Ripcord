using System.Globalization;
using Ripcord.Domain.Diagnostics;

namespace Ripcord.Domain.Deployment;

/// One line on why the listener is not running, and what to type next. Null `Why` when there
/// is nothing to explain.
public sealed record ServiceVerdict(string? Why, IReadOnlyList<string> Next)
{
    public static ServiceVerdict None { get; } = new(null, []);

    public bool Equals(ServiceVerdict? other) =>
        other is not null && this.Why == other.Why && Structural.Same(this.Next, other.Next);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(this.Why);
        Structural.Add(ref hash, this.Next);
        return hash.ToHashCode();
    }
}

/// Reads what Windows and the listener's log say about a stopped service. Every fact that
/// could not be read is "cannot tell", never "stopped": a wrong verdict here sends somebody
/// to fix the wrong thing on the day it matters.
public static class ServiceDiagnosis
{
    /// Where a service that died before its log could open left its reason. Short enough to
    /// type from the screen: each fits the console under a four-column indent.
    public static IReadOnlyList<string> EventLogCommands { get; } =
    [
        $"Get-WinEvent -ProviderName {DeploymentPlan.EventSource} -MaxEvents 5 | fl",
        "Get-WinEvent -ProviderName '.NET Runtime' -MaxEvents 5 | fl",
    ];

    private const string Check = "ripcord check";

    private const string InstallDryRun = "ripcord service install --dry-run";

    private const string Restart = "ripcord service restart";

    public static string Meaning(ExitCode code) => code switch
    {
        ExitCode.Success => "a clean stop",
        ExitCode.CriticalFinding => "it reported a critical finding",
        ExitCode.InvalidConfiguration => "the configuration did not load",
        ExitCode.LocalAccessFailure =>
            "it could not reach a local resource: log folder, certificate or port",
        ExitCode.Refused => "a precondition refused it",
        ExitCode.IntermediateState => "it stopped part-way through a change",
        _ => string.Create(CultureInfo.InvariantCulture, $"exit {(int)code}"),
    };

    /// The code as `sc.exe query` prints it, and what it means.
    public static string WindowsMeaning(ServiceExit exit)
    {
        ArgumentNullException.ThrowIfNull(exit);

        string code = exit.Win32ExitCode > 0xFFFF
            ? string.Create(CultureInfo.InvariantCulture, $"0x{exit.Win32ExitCode:X8}")
            : exit.Win32ExitCode.ToString(CultureInfo.InvariantCulture);

        return exit.Kind switch
        {
            ServiceExitKind.Clean => $"{code}, a normal stop",
            ServiceExitKind.Ripcord when exit.Code is { } ripcord =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{code}, Ripcord exit {(int)ripcord}: {Meaning(ripcord)}"),
            ServiceExitKind.Crashed => $"{code}, the process ended without reporting to Windows",
            _ => exit.Win32ExitCode switch
            {
                2 => $"{code}, set by Windows: the binary was not found",
                1053 => $"{code}, set by Windows: it did not answer the start request",
                1069 => $"{code}, set by Windows: the service account could not log on",
                _ => $"{code}, set by Windows, not by Ripcord",
            },
        };
    }

    public static ServiceVerdict Diagnose(
        ObservedService service, bool? logsWritable, LogReading? log)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (!service.Installed)
        {
            return new ServiceVerdict("it is not installed", [InstallDryRun]);
        }

        return service.State switch
        {
            ServiceRunState.Running => ServiceVerdict.None,
            ServiceRunState.StartPending =>
                new ServiceVerdict("Windows is starting it: look again in a few seconds", []),
            ServiceRunState.StopPending =>
                new ServiceVerdict("Windows is stopping it: look again in a few seconds", []),
            ServiceRunState.Paused =>
                new ServiceVerdict("it is paused, which Ripcord never does itself", []),
            ServiceRunState.Stopped => Stopped(service, logsWritable, log),
            _ => new ServiceVerdict(
                service.Unreadable is { } reason
                    ? $"Windows did not say whether it runs: {reason}"
                    : "Windows did not say whether it runs",
                []),
        };
    }

    /// The log is believed before Windows: it says which run it is about, and Windows keeps
    /// the exit code of whichever stop came last.
    private static ServiceVerdict Stopped(
        ObservedService service, bool? logsWritable, LogReading? log)
    {
        if (service.IsDisabled)
        {
            return new ServiceVerdict(
                "its start mode is Disabled: Windows will not start it",
                [$"sc.exe config {DeploymentPlan.ServiceName} start= auto", Restart]);
        }

        ListenerLogSummary summary = log is { Unreadable: null }
            ? ListenerLog.Summarise(log.Lines)
            : ListenerLogSummary.Empty;

        if (summary.Exit is { } logged and not ExitCode.Success)
        {
            return Exited(logged, "it stopped with");
        }

        if (summary.Crashed)
        {
            return new ServiceVerdict("it stopped on an unhandled error, written to its log", []);
        }

        if (summary.Exit is ExitCode.Success || summary.Cancelled)
        {
            return new ServiceVerdict(
                "it stopped cleanly: by an operator, at shutdown, or because the listener "
                    + "is disabled in ripcord.yaml",
                [Restart]);
        }

        if (log is null && logsWritable == false)
        {
            return new ServiceVerdict(
                $"{DeploymentPlan.ServiceAccount} cannot write its logs folder, so it stops "
                    + "as soon as it starts",
                [InstallDryRun]);
        }

        ServiceExit? windows = service.LastExit;

        if (windows is { Kind: ServiceExitKind.Ripcord, Code: { } recorded })
        {
            return Exited(recorded, "Windows recorded");
        }

        if (windows is { Kind: ServiceExitKind.Crashed })
        {
            return new ServiceVerdict(
                "the process ended without reporting to Windows", EventLogCommands);
        }

        if (summary.EndedSilently)
        {
            return new ServiceVerdict("it started, then stopped without writing why", EventLogCommands);
        }

        if (windows is { Kind: ServiceExitKind.Other })
        {
            return new ServiceVerdict($"Windows stopped it: {WindowsMeaning(windows)}", EventLogCommands);
        }

        if (log is { Unreadable: { } unreadable })
        {
            return new ServiceVerdict($"its log could not be read: {unreadable}", []);
        }

        return log is null
            ? new ServiceVerdict(
                "no log today or yesterday: it died before it could write one, or stopped "
                    + "before then",
                EventLogCommands)
            : new ServiceVerdict("its log does not say why it stopped", EventLogCommands);
    }

    private static ServiceVerdict Exited(ExitCode code, string source) =>
        new(
            string.Create(
                CultureInfo.InvariantCulture, $"{source} exit {(int)code}: {Meaning(code)}"),
            code switch
            {
                ExitCode.InvalidConfiguration => [Check],
                ExitCode.LocalAccessFailure => [Check, InstallDryRun],
                _ => [],
            });
}
