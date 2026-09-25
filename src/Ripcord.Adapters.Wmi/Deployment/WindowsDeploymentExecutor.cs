using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Ripcord.Domain.Deployment;
using Ripcord.Ports.Deployment;

namespace Ripcord.Adapters.Wmi.Deployment;

/// Runs the Windows commands a deployment step names. It looks and it acts; what needs doing
/// was already decided by DeploymentPlan, which runs anywhere and is tested.
///
/// sc.exe, netsh and icacls rather than the Win32 APIs: these are the exact commands the
/// documentation tells an operator to run by hand, so the dry-run output and what actually
/// happens cannot drift apart.
public sealed class WindowsDeploymentExecutor : IDeploymentExecutor
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan CimTimeout = TimeSpan.FromSeconds(15);

    public ObservedDeployment Observe(DesiredDeployment desired, ObservedService service)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(service);

        (int ruleCode, string ruleOutput) = Run(
            "netsh",
            $"advfirewall firewall show rule name=\"{DeploymentPlan.FirewallRuleName}\" verbose");

        bool ruleInstalled = ruleCode == 0;

        // Unreadable is reported as not running: the opposite default would leave a stopped
        // listener alone and call the deployment correct. The cost is one redundant `sc start`,
        // which `ServiceCommand` reads as the state the step was asking for.
        return new ObservedDeployment(
            service.Installed,
            service.BinaryPath,
            ruleInstalled,
            ruleInstalled ? PortIn(ruleOutput) : null,
            ruleInstalled ? ValueAfter(ruleOutput, "RemoteIP") : null,
            SnapshotReadable(desired.SnapshotFolder),
            service.State == ServiceRunState.Running,
            LogsWritable(desired.LogsFolder),
            EventSourceRegistered(),
            desired.SnapshotVolume.Length == 0 || Directory.Exists(desired.SnapshotVolume),
            SnapshotWrittenAt(desired),
            KeyFile(desired) is { } key
                ? AccessControl.GrantsRead(Run("icacls", $"\"{key}\"").Output, DeploymentPlan.ServiceAccount)
                : null);
    }

    /// The file holding the certificate's private key: a CNG key under `Crypto\Keys`, a
    /// legacy CSP one under `Crypto\RSA\MachineKeys`. Null when any link is missing.
    /// Unverified on a host (V78).
    private static string? KeyFile(DesiredDeployment desired)
    {
        if (desired.CertificateThumbprint is not { } thumbprint)
        {
            return null;
        }

        try
        {
            using X509Store store = new(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);

            X509Certificate2Collection found =
                store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);

            try
            {
                return found.Count == 0 ? null : KeyFileOf(found[0]);
            }
            finally
            {
                foreach (X509Certificate2 certificate in found)
                {
                    certificate.Dispose();
                }
            }
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string? KeyFileOf(X509Certificate2 certificate)
    {
        using AsymmetricAlgorithm? key =
            (AsymmetricAlgorithm?)certificate.GetRSAPrivateKey() ?? certificate.GetECDsaPrivateKey();

        string? unique = key switch
        {
            RSACng rsa => rsa.Key.UniqueName,
            ECDsaCng ecdsa => ecdsa.Key.UniqueName,
            RSACryptoServiceProvider csp => csp.CspKeyContainerInfo.UniqueKeyContainerName,
            _ => null,
        };

        if (unique is null)
        {
            return null;
        }

        string crypto = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft",
            "Crypto");

        return new[]
            {
                Path.Combine(crypto, "Keys", unique),
                Path.Combine(crypto, "RSA", "MachineKeys", unique),
            }
            .FirstOrDefault(File.Exists);
    }

    /// Only a path with a folder is looked at: a bare file name would be read against this
    /// process's working directory, not the listener's.
    private static DateTimeOffset? SnapshotWrittenAt(DesiredDeployment desired) =>
        desired.SnapshotFolder.Length > 0 && File.Exists(desired.SnapshotPath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(desired.SnapshotPath), TimeSpan.Zero)
            : null;

    /// `Win32_Service` rather than `sc query`: `sc` prints its labels and state words in the
    /// host's language, and a French Windows would answer something unreadable for ever.
    /// `State` and `StartMode` are invariant strings (V59).
    ///
    /// The registry says whether it is installed and what it runs when CIM cannot be read, so
    /// an unreadable WMI still shows the service rather than "not installed".
    public ObservedService ObserveService()
    {
        string? imagePath = ServiceImagePath();

        try
        {
            using CimSession session = CimSession.Create(computerName: null);
            using CimOperationOptions options = new() { Timeout = CimTimeout };

            CimInstance? service = session
                .QueryInstances(
                    @"root\cimv2",
                    "WQL",
                    "SELECT State, StartMode, ExitCode, ServiceSpecificExitCode, PathName "
                    + $"FROM Win32_Service WHERE Name = '{DeploymentPlan.ServiceName}'",
                    options)
                .FirstOrDefault();

            using (service)
            {
                if (service is null)
                {
                    return imagePath is null
                        ? ObservedService.Absent
                        : Unread(imagePath, "Win32_Service does not list it");
                }

                return new ObservedService(
                    true,
                    Text(service, "PathName") ?? imagePath,
                    ObservedService.ParseState(Text(service, "State")),
                    Text(service, "StartMode"),
                    Code(service, "ExitCode"),
                    Code(service, "ServiceSpecificExitCode"));
            }
        }
        catch (CimException exception)
        {
            // Said in the Domain's words: the message itself is in the host's language.
            string reason = exception.NativeErrorCode == NativeErrorCode.AccessDenied
                ? ObservedService.AccessDenied
                : exception.Message;

            return imagePath is null ? ObservedService.Absent : Unread(imagePath, reason);
        }
    }

    private static ObservedService Unread(string imagePath, string reason) =>
        new(true, imagePath, ServiceRunState.Unknown, null, null, null, reason);

    private static string? Text(CimInstance instance, string name) =>
        instance.CimInstanceProperties[name]?.Value as string;

    /// UInt32 in the MOF; Ripcord's codes carry the customer bit, which still fits an int.
    private static int? Code(CimInstance instance, string name) =>
        instance.CimInstanceProperties[name]?.Value switch
        {
            uint code => unchecked((int)code),
            int code => code,
            _ => null,
        };

    public void Apply(DeploymentStep change, DesiredDeployment desired)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(desired);

        // icacls cannot grant on a path that does not exist, and a host being deployed for
        // the first time may have no snapshot folder until the first `ripcord status` writes one.
        if (change.Action == DeploymentAction.GrantSnapshotAccess)
        {
            Directory.CreateDirectory(desired.SnapshotFolder);
        }

        if (change.Action == DeploymentAction.GrantLogsAccess)
        {
            Directory.CreateDirectory(desired.LogsFolder);
        }

        foreach ((string file, string arguments) in change.Action switch
        {
            DeploymentAction.GrantKeyAccess or DeploymentAction.RevokeKeyAccess =>
                KeyCommands(change.Action, desired),
            _ => CommandsFor(change.Action, desired),
        })
        {
            (int code, string output) = Run(file, arguments);

            if (code != 0 && ServiceCommand.LeavesNothingToDo(change.Action, code))
            {
                continue;
            }

            if (code != 0)
            {
                throw new InvalidOperationException(
                    $"'{file} {arguments}' exited with {code}: {output.Trim()}");
            }
        }

        if (DeploymentPlan.StartsTheService(change.Action))
        {
            this.WatchTheStart();
        }
    }

    private static readonly TimeSpan StartWatch = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan StartWatchInterval = TimeSpan.FromMilliseconds(500);

    /// `sc start` returns once the process has answered the service manager. Every failure the
    /// listener can report comes after that, so the step watches it for a few seconds.
    private void WatchTheStart()
    {
        Stopwatch watch = Stopwatch.StartNew();

        while (watch.Elapsed < StartWatch)
        {
            Thread.Sleep(StartWatchInterval);

            if (ServiceDiagnosis.StartFailure(this.ObserveService()) is { } failure)
            {
                throw new InvalidOperationException(failure);
            }
        }
    }

    /// The service runs as a virtual account, which has no password to manage and no rights
    /// beyond what it is granted — it is never a member of Hyper-V Administrators.
    private static IEnumerable<(string File, string Arguments)> CommandsFor(
        DeploymentAction action, DesiredDeployment desired) => action switch
    {
        DeploymentAction.CreateService =>
        [
            ("sc.exe",
                $"create {DeploymentPlan.ServiceName} "
                + $"binPath= \"\\\"{desired.BinaryPath}\\\" serve\" start= auto "
                + $"obj= \"{DeploymentPlan.ServiceAccount}\""),
        ],

        DeploymentAction.StartService =>
        [
            ("sc.exe", $"start {DeploymentPlan.ServiceName}"),
        ],

        DeploymentAction.RestartService =>
        [
            ("sc.exe", $"stop {DeploymentPlan.ServiceName}"),
            ("sc.exe", $"start {DeploymentPlan.ServiceName}"),
        ],

        DeploymentAction.UpdateService =>
        [
            ("sc.exe", $"stop {DeploymentPlan.ServiceName}"),
            ("sc.exe",
                $"config {DeploymentPlan.ServiceName} "
                + $"binPath= \"\\\"{desired.BinaryPath}\\\" serve\""),
            ("sc.exe", $"start {DeploymentPlan.ServiceName}"),
        ],

        DeploymentAction.RemoveService =>
        [
            ("sc.exe", $"stop {DeploymentPlan.ServiceName}"),
            ("sc.exe", $"delete {DeploymentPlan.ServiceName}"),
        ],

        DeploymentAction.CreateFirewallRule =>
        [
            ("netsh", FirewallRule("add", desired)),
        ],

        DeploymentAction.UpdateFirewallRule =>
        [
            ("netsh", $"advfirewall firewall delete rule name=\"{DeploymentPlan.FirewallRuleName}\""),
            ("netsh", FirewallRule("add", desired)),
        ],

        DeploymentAction.RemoveFirewallRule =>
        [
            ("netsh", $"advfirewall firewall delete rule name=\"{DeploymentPlan.FirewallRuleName}\""),
        ],

        DeploymentAction.GrantSnapshotAccess =>
        [
            ("icacls",
                $"\"{desired.SnapshotFolder}\" /grant \"{DeploymentPlan.ServiceAccount}\":(OI)(CI)(R)"),
        ],

        DeploymentAction.RevokeSnapshotAccess =>
        [
            // `/t` because the grant was inheritable: it has already propagated to the
            // snapshot file, and removing it from the folder alone would leave the account
            // still able to read what is in there.
            ("icacls",
                $"\"{desired.SnapshotFolder}\" /remove \"{DeploymentPlan.ServiceAccount}\" /t"),
        ],

        // Modify on this folder only, never on the install folder or the binary beside it.
        DeploymentAction.GrantLogsAccess =>
        [
            ("icacls",
                $"\"{desired.LogsFolder}\" /grant \"{DeploymentPlan.ServiceAccount}\":(OI)(CI)(M)"),
        ],

        DeploymentAction.RevokeLogsAccess =>
        [
            ("icacls",
                $"\"{desired.LogsFolder}\" /remove \"{DeploymentPlan.ServiceAccount}\" /t"),
        ],

        // What EventLog.CreateEventSource writes, as a command an operator could type. The
        // message file is the framework's, present on every Windows Server.
        DeploymentAction.RegisterEventSource =>
        [
            ("reg.exe",
                $"add \"HKLM\\{EventSourceKey}\" /v EventMessageFile /t REG_EXPAND_SZ "
                + $"/d \"{EventMessageFile}\" /f"),
        ],

        DeploymentAction.RemoveEventSource =>
        [
            ("reg.exe", $"delete \"HKLM\\{EventSourceKey}\" /f"),
        ],

        // A new action nobody mapped must fail loudly, not run some other step's command.
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    /// Read only, on the key file only: the account signs with the key, it never replaces it.
    private static IEnumerable<(string File, string Arguments)> KeyCommands(
        DeploymentAction action, DesiredDeployment desired)
    {
        string key = KeyFile(desired)
            ?? throw new InvalidOperationException(
                $"the private key of certificate {desired.CertificateThumbprint} was not found "
                + @"in LocalMachine\My");

        return action == DeploymentAction.GrantKeyAccess
            ? [("icacls", $"\"{key}\" /grant \"{DeploymentPlan.ServiceAccount}\":(R)")]
            : [("icacls", $"\"{key}\" /remove \"{DeploymentPlan.ServiceAccount}\"")];
    }

    private const string EventSourceKey =
        $@"SYSTEM\CurrentControlSet\Services\EventLog\Application\{DeploymentPlan.EventSource}";

    private const string EventMessageFile =
        @"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll";

    private static string FirewallRule(string verb, DesiredDeployment desired) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"advfirewall firewall {verb} rule name=\"{DeploymentPlan.FirewallRuleName}\" "
            + $"dir=in action=allow protocol=TCP localport={desired.Port} "
            + $"remoteip={desired.PeerAddress}");

    /// The service account's access is what matters, and only Windows can answer that. The
    /// folder being absent is not the same as being unreadable, but it needs the same step.
    ///
    /// What the answer means is AccessControl's: an entry denying the account also names it,
    /// and a substring search would read that as access the service does not have.
    private static bool SnapshotReadable(string snapshotFolder) =>
        Directory.Exists(snapshotFolder)
        && AccessControl.GrantsRead(
            Run("icacls", $"\"{snapshotFolder}\"").Output, DeploymentPlan.ServiceAccount);

    /// Modify, not merely write: pruning an old log deletes it.
    private static bool LogsWritable(string logsFolder) =>
        Directory.Exists(logsFolder)
        && AccessControl.GrantsModify(
            Run("icacls", $"\"{logsFolder}\"").Output, DeploymentPlan.ServiceAccount);

    private static bool EventSourceRegistered()
    {
        using Microsoft.Win32.RegistryKey? key =
            Microsoft.Win32.Registry.LocalMachine.OpenSubKey(EventSourceKey);

        return key is not null;
    }

    /// From the registry, not from `sc qc`, whose labels are localised.
    private static string? ServiceImagePath()
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{DeploymentPlan.ServiceName}");

        return key?.GetValue("ImagePath") as string;
    }

    /// Both streams are drained concurrently against the deadline. Reading one to the end
    /// before waiting would let a wedged child hang here forever — and a child that fills the
    /// other pipe meanwhile would deadlock both sides.
    private static (int Code, string Output) Run(string file, string arguments)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();

        using CancellationTokenSource deadline = new(CommandTimeout);

        try
        {
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);
            Task<string> standardError = process.StandardError.ReadToEndAsync(deadline.Token);

            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();

            return (process.ExitCode, standardOutput.Result + standardError.Result);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{file} {arguments}' did not finish in {CommandTimeout}");
        }
    }

    /// `netsh`'s field labels are localised too, so an unparseable rule is reported as not
    /// matching rather than as matching. That costs one redundant rule rewrite per run on a
    /// non-English host; reporting it as correct could leave a rule open to the wrong address.
    /// See V23.
    private static string? ValueAfter(string output, string label)
    {
        foreach (string line in output.Split('\n'))
        {
            int separator = line.IndexOf(':', StringComparison.Ordinal);

            if (separator > 0
                && line[..separator].Trim().Equals(label, StringComparison.OrdinalIgnoreCase))
            {
                return line[(separator + 1)..].Trim().Trim('"');
            }
        }

        return null;
    }

    private static int? PortIn(string output) =>
        int.TryParse(ValueAfter(output, "LocalPort"), CultureInfo.InvariantCulture, out int port)
            ? port
            : null;
}
