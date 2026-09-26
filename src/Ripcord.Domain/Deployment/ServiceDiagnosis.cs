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

    private const string Uninstall = "ripcord service uninstall";

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

    /// ERROR_SERVICE_NEVER_STARTED: nothing has asked Windows to start it since it booted.
    public const int NeverStarted = 1077;

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
                2 => $"{code}: the binary was not found",
                1053 => $"{code}: it did not answer the start request in time",
                1069 => $"{code}: the service account could not log on",
                NeverStarted => $"{code}: it has not been started since Windows booted",
                _ => $"{code}, a Windows code, not Ripcord's",
            },
        };
    }

    /// Read just after `sc start` came back: it returns once the process has answered the
    /// service manager, not once the listener has come up, so a listener that stops at once
    /// looks started. Null while it runs, starts or cannot be read.
    public static string? StartFailure(ObservedService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (!service.Installed || service.State != ServiceRunState.Stopped)
        {
            return null;
        }

        return service.LastExit is { } exit
            ? $"the service stopped right after it started: {WindowsMeaning(exit)}"
            : "the service stopped right after it started";
    }

    public static ServiceVerdict Diagnose(
        RipcordService which,
        ObservedService service,
        bool? logsWritable,
        LogReading? log,

        /// `listener.enabled: false` in the configuration this run read.
        bool listenerDisabled = false,
        RunningBuild? build = null)
    {
        ArgumentNullException.ThrowIfNull(which);
        ArgumentNullException.ThrowIfNull(service);

        if (!service.Installed)
        {
            return new ServiceVerdict("it is not installed", [InstallDryRun]);
        }

        return service.State switch
        {
            ServiceRunState.Running => Running(build),
            ServiceRunState.StartPending =>
                new ServiceVerdict("Windows is starting it: look again in a few seconds", []),
            ServiceRunState.StopPending =>
                new ServiceVerdict("Windows is stopping it: look again in a few seconds", []),
            ServiceRunState.Paused =>
                new ServiceVerdict("it is paused, which Ripcord never does itself", []),
            ServiceRunState.Stopped =>
                Stopped(service, logsWritable, log, listenerDisabled, which),
            _ => Unknown(service.Unreadable),
        };
    }

    /// `ripcord update` replaces the file, not the process: the old build serves until then.
    private static ServiceVerdict Running(RunningBuild? build) =>
        build is { Outdated: true, Build: { } running }
            ? new ServiceVerdict(
                $"it still runs {running}, not the build on disk: restart it to run the one installed",
                [Restart])
            : ServiceVerdict.None;

    private static ServiceVerdict Unknown(string? reason) => new(Undescribed(reason), []);

    /// Shared with `ripcord status`, so both commands say the same thing about the same read.
    public static string Undescribed(string? reason) => reason switch
    {
        ObservedService.AccessDenied =>
            "Windows did not say whether it runs: access is denied. Run this again from an "
                + "elevated console.",
        { } other => $"Windows did not say whether it runs: {other}",
        null => "Windows did not say whether it runs",
    };

    /// Windows holds the latest stop; the log only the last run that got as far as its
    /// banner. A start that fails before the banner leaves the file as an earlier run left it,
    /// so the log is believed only where Windows does not contradict it.
    private static ServiceVerdict Stopped(
        ObservedService service, bool? logsWritable, LogReading? log, bool listenerDisabled, RipcordService which)
    {
        if (service.IsDisabled)
        {
            return new ServiceVerdict(
                "its start mode is Disabled: Windows will not start it",
                [$"sc.exe config {which.Name} start= auto", Restart]);
        }

        if (listenerDisabled)
        {
            return new ServiceVerdict(
                "the listener is disabled in ripcord.yaml (listener.enabled: false)", [Uninstall]);
        }

        // Before the log: a start that cannot open it writes nothing there.
        if (logsWritable == false)
        {
            return new ServiceVerdict(
                $"{which.Account} cannot write its logs folder, so it stops "
                    + "as soon as it starts",
                [InstallDryRun]);
        }

        ServiceLogSummary summary = log is { Unreadable: null }
            ? ServiceLog.Summarise(log.Lines)
            : ServiceLogSummary.Empty;

        ServiceExit? windows = service.LastExit;

        if (windows is { Kind: ServiceExitKind.Other, Win32ExitCode: NeverStarted })
        {
            return new ServiceVerdict("it has not been started since Windows booted", [Restart]);
        }

        if (windows is { Kind: ServiceExitKind.Other } other)
        {
            return new ServiceVerdict(
                // These codes are set before the listener could write its banner.
                $"Windows recorded {WindowsMeaning(other)}"
                    + (summary.SawStart || summary.Said ? EarlierRun : ""),
                EventLogCommands);
        }

        if (windows is { Kind: ServiceExitKind.Ripcord, Code: { } recorded }
            && !Agrees(summary, recorded))
        {
            // A banner with no outcome may be this very run: only an outcome that contradicts
            // Windows dates the log.
            return Exited(
                recorded, "Windows recorded", summary.Said ? EarlierRun : "");
        }

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
                "it stopped cleanly: by an operator or at shutdown", [Restart]);
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

    private const string EarlierRun = "; its log is from an earlier run";

    /// A crash is logged, then stopped with exit 3 by the host.
    private static bool Agrees(ServiceLogSummary summary, ExitCode recorded) =>
        summary.Exit == recorded
        || (summary.Crashed && summary.Exit is null && recorded == ExitCode.LocalAccessFailure);

    private static ServiceVerdict Exited(ExitCode code, string source, string note = "") =>
        new(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{source} exit {(int)code}: {Meaning(code)}{note}"),
            code switch
            {
                ExitCode.InvalidConfiguration => [Check],
                ExitCode.LocalAccessFailure => [Check, InstallDryRun],
                _ => [],
            });
}
