using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Ripcord.Domain;
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

        string? keyFile = KeyFile(desired);

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
            SnapshotWrittenAt(desired),
            keyFile is not null
                ? AccessControl.GrantsRead(Run("icacls", $"\"{keyFile}\"").Output, RipcordService.Listener.Account)
                : null,
            keyFile,
            KeyFilesGranted(),
            [.. DeploymentPlan.StaleFolderCandidates(desired, service.BinaryPath)
                .Where(folder => Directory.Exists(folder)
                    && AccessControl.GrantsExplicitly(
                        Run("icacls", $"\"{folder}\"").Output, RipcordService.Listener.Account))],
            AccessControl.GrantsRead(
                Run("icacls", $"\"{WindowsPath.Join(desired.InstallFolder, "ripcord.yaml")}\"").Output,
                RipcordService.Listener.Account),
            AccessControl.GrantsExplicitly(
                Run("icacls", $"\"{desired.InstallFolder}\"").Output, RipcordService.Listener.Account),
            this.ObservePublisher(desired));
    }

    /// `/c` because without it icacls stops at the first key it cannot read (SYSTEM-only keys,
    /// not ours anyway). A folder of thousands of keys can outlast the timeout: nothing is then
    /// known, so nothing is revoked, and the rest of the deployment is not held up for it.
    private static IReadOnlyList<string>? KeyFilesGranted()
    {
        try
        {
            return [.. KeyFolders.SelectMany(folder => AccessControl.FilesGrantingExplicitly(
                Run("icacls", $"\"{folder}\\*\" /c").Output, folder, RipcordService.Listener.Account))];
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// Where machine keys live: CNG, then legacy CSP.
    private static readonly string[] KeyFolders =
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Crypto", "Keys"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Crypto", "RSA", "MachineKeys"),
    ];

    /// The file holding the certificate's private key: a CNG key under `Crypto\Keys`, a
    /// legacy CSP one under `Crypto\RSA\MachineKeys`. Null when any link is missing.
    /// Unverified on a host (V69).
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
    public ObservedService ObserveService(RipcordService which)
    {
        ArgumentNullException.ThrowIfNull(which);

        string? imagePath = ServiceImagePath(which);

        try
        {
            using CimSession session = CimSession.Create(computerName: null);
            using CimOperationOptions options = new() { Timeout = CimTimeout };

            CimInstance? service = session
                .QueryInstances(
                    @"root\cimv2",
                    "WQL",
                    "SELECT State, StartMode, ExitCode, ServiceSpecificExitCode, PathName, ProcessId "
                    + $"FROM Win32_Service WHERE Name = '{which.Name}'",
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
                    Code(service, "ServiceSpecificExitCode"),
                    ProcessId: Code(service, "ProcessId") is int id and > 0 ? id : null);
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
            Directory.CreateDirectory(LogsOf(change, desired));
        }

        if (change.Action == DeploymentAction.GrantSnapshotWriteAccess)
        {
            Directory.CreateDirectory(desired.SnapshotFolder);
        }

        if (change.Action is DeploymentAction.GrantEncryptionNamespaceAccess
            or DeploymentAction.RevokeEncryptionNamespaceAccess)
        {
            EditEncryptionNamespace(change.Subject, change.Action == DeploymentAction.GrantEncryptionNamespaceAccess);
            return;
        }

        foreach ((string file, string arguments) in change.Action switch
        {
            DeploymentAction.GrantKeyAccess or DeploymentAction.RevokeKeyAccess =>
                KeyCommands(change.Action, desired),
            DeploymentAction.RevokeStaleKeyAccess or DeploymentAction.RevokeStaleFolderAccess =>
            [
                ("icacls",
                    $"\"{change.Target}\" /remove \"{RipcordService.Listener.Account}\""
                    + (change.Recursive ? " /t" : "")),
            ],
            _ => CommandsFor(change, desired),
        })
        {
            (int code, string output) = Run(file, arguments);

            if (code != 0
                && (ServiceCommand.LeavesNothingToDo(change.Action, code)
                    || (change.Action is DeploymentAction.AddToHyperVAdministrators
                            or DeploymentAction.RemoveFromHyperVAdministrators
                        && LocalGroup.LeavesNothingToDo(
                            change.Action == DeploymentAction.AddToHyperVAdministrators, output))))
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
            this.WatchTheStart(change.Subject);
        }
        else if (change.Action == DeploymentAction.StopService)
        {
            this.WaitForTheStop(change.Subject);
        }
    }

    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(30);

    /// `sc stop` returns while the stop is still pending: the step is done once Windows says so.
    private void WaitForTheStop(RipcordService which)
    {
        Stopwatch watch = Stopwatch.StartNew();
        ObservedService service = this.ObserveService(which);

        while (service.State != ServiceRunState.Stopped && watch.Elapsed < StopWait)
        {
            Thread.Sleep(StartWatchInterval);
            service = this.ObserveService(which);
        }

        if (service.State != ServiceRunState.Stopped)
        {
            throw new InvalidOperationException(
                $"Windows still reports the service as {service.State} after {StopWait.TotalSeconds:0} s");
        }
    }

    private static readonly TimeSpan StartWatch = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan StartWatchInterval = TimeSpan.FromMilliseconds(500);

    /// `sc start` returns once the process has answered the service manager. Every failure the
    /// listener can report comes after that, so the step watches it for a few seconds.
    private void WatchTheStart(RipcordService which)
    {
        Stopwatch watch = Stopwatch.StartNew();

        while (watch.Elapsed < StartWatch)
        {
            Thread.Sleep(StartWatchInterval);

            if (ServiceDiagnosis.StartFailure(this.ObserveService(which)) is { } failure)
            {
                throw new InvalidOperationException(failure);
            }
        }
    }

    /// Each service runs as its own virtual account, which has no password to manage and no
    /// rights beyond what it is granted. Only the publisher is ever put in Hyper-V
    /// Administrators; the listener, which faces the network, never is.
    private static IEnumerable<(string File, string Arguments)> CommandsFor(
        DeploymentStep change, DesiredDeployment desired)
    {
        RipcordService service = change.Subject;
        string name = service.Name;
        string account = service.Account;
        string binPath = $"binPath= \"\\\"{desired.BinaryPath}\\\" {service.Verb}\"";
        bool publisher = service == RipcordService.Publisher;

        return change.Action switch
        {
            DeploymentAction.CreateService =>
            [
                ("sc.exe", $"create {name} {binPath} start= auto obj= \"{account}\""),
            ],

            DeploymentAction.StartService => [("sc.exe", $"start {name}")],

            DeploymentAction.RestartService =>
            [
                ("sc.exe", $"stop {name}"),
                ("sc.exe", $"start {name}"),
            ],

            DeploymentAction.UpdateService =>
            [
                ("sc.exe", $"stop {name}"),
                ("sc.exe", $"config {name} {binPath}"),
                ("sc.exe", $"start {name}"),
            ],

            DeploymentAction.StopService => [("sc.exe", $"stop {name}")],

            DeploymentAction.RemoveService =>
            [
                ("sc.exe", $"stop {name}"),
                ("sc.exe", $"delete {name}"),
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
                ("icacls", $"\"{desired.SnapshotFolder}\" /grant \"{account}\":(OI)(CI)(R)"),
            ],

            // Modify on the snapshot folder, which holds nothing but the snapshot.
            DeploymentAction.GrantSnapshotWriteAccess =>
            [
                ("icacls", $"\"{desired.SnapshotFolder}\" /grant \"{account}\":(OI)(CI)(M)"),
            ],

            // `(OI)(NP)`: the files directly in the folder, so a `ripcord.yaml` rewritten by
            // moving a new file into place is covered too; nothing in the folders below it.
            DeploymentAction.GrantConfigurationAccess =>
            [
                ("icacls", $"\"{desired.InstallFolder}\" /grant \"{account}\":(OI)(NP)(R)"),
            ],

            // Never `/t`: that would also strip the explicit grants on `logs` below it.
            DeploymentAction.RevokeConfigurationAccess =>
            [
                ("icacls", $"\"{desired.InstallFolder}\" /remove \"{account}\""),
            ],

            // `/t` because the grant was inheritable: it has already propagated to the
            // snapshot file, and removing it from the folder alone would leave the account
            // still able to read what is in there.
            DeploymentAction.RevokeSnapshotAccess =>
            [
                ("icacls", $"\"{desired.SnapshotFolder}\" /remove \"{account}\" /t"),
            ],

            // The publisher's folder stops inheriting from `logs`, where the listener may
            // modify, before the publisher is granted modify on it.
            DeploymentAction.GrantLogsAccess when publisher =>
            [
                ("icacls", $"\"{desired.PublisherLogsFolder}\" /inheritance:d"),
                ("icacls", $"\"{desired.PublisherLogsFolder}\" /remove \"{RipcordService.Listener.Account}\""),
                ("icacls", $"\"{desired.PublisherLogsFolder}\" /grant \"{account}\":(OI)(CI)(M)"),
            ],

            // Modify on this folder only, never on the install folder or the binary beside it.
            DeploymentAction.GrantLogsAccess =>
            [
                ("icacls", $"\"{desired.LogsFolder}\" /grant \"{account}\":(OI)(CI)(M)"),
            ],

            DeploymentAction.RevokeLogsAccess =>
            [
                ("icacls", $"\"{LogsOf(change, desired)}\" /remove \"{account}\" /t"),
            ],

            DeploymentAction.AddToHyperVAdministrators =>
            [
                ("net.exe", $"localgroup \"{change.Target}\" \"{account}\" /add"),
            ],

            DeploymentAction.RemoveFromHyperVAdministrators =>
            [
                ("net.exe", $"localgroup \"{change.Target}\" \"{account}\" /delete"),
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
            _ => throw new ArgumentOutOfRangeException(nameof(change), change.Action, null),
        };
    }

    private const string EncryptionNamespace = @"root\cimv2\Security\MicrosoftVolumeEncryption";

    /// The publisher's side of the deployment. It looks and reports; the Domain decides.
    private ObservedPublisher ObservePublisher(DesiredDeployment desired)
    {
        RipcordService publisher = RipcordService.Publisher;
        ObservedService service = this.ObserveService(publisher);
        string? group = HyperVAdministratorsName();

        bool? member = null;

        if (group is not null && Run("net.exe", $"localgroup \"{group}\"") is (0, string members))
        {
            member = LocalGroup.HasMember(members, publisher.Account);
        }

        (NamespaceGrant? encryption, string? note) = EncryptionAccess(publisher);
        string yaml = WindowsPath.Join(desired.InstallFolder, "ripcord.yaml");
        string logs = desired.PublisherLogsFolder;
        string logsAccess = Directory.Exists(logs) ? Run("icacls", $"\"{logs}\"").Output : "";

        return new ObservedPublisher(
            service,
            AccessControl.GrantsRead(Run("icacls", $"\"{yaml}\"").Output, publisher.Account),
            Directory.Exists(desired.SnapshotFolder)
                && AccessControl.GrantsModify(
                    Run("icacls", $"\"{desired.SnapshotFolder}\"").Output, publisher.Account),
            AccessControl.GrantsModify(logsAccess, publisher.Account),
            AccessControl.GrantsModify(logsAccess, RipcordService.Listener.Account),
            AccessControl.GrantsExplicitly(
                Run("icacls", $"\"{desired.InstallFolder}\"").Output, publisher.Account),
            group,
            member,
            encryption,
            note);
    }

    /// Hyper-V Administrators by its SID: "Administrateurs Hyper-V" on a French host. Null
    /// when this host has no such group.
    private static string? HyperVAdministratorsName()
    {
        try
        {
            string qualified = new SecurityIdentifier(LocalGroup.HyperVAdministratorsSid)
                .Translate(typeof(NTAccount)).Value;

            return qualified[(qualified.IndexOf('\\', StringComparison.Ordinal) + 1)..];
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    /// The account's SID from Windows, checked against the formula the plan named it by.
    private static string SidOf(RipcordService service)
    {
        string computed = VirtualAccount.Sid(service.Name);

        try
        {
            string actual = new NTAccount(service.Account).Translate(typeof(SecurityIdentifier)).Value;

            return string.Equals(actual, computed, StringComparison.OrdinalIgnoreCase)
                ? actual
                : throw new InvalidOperationException(
                    $"Windows gives {service.Account} the SID {actual}, not {computed}: nothing was changed");
        }
        catch (IdentityNotMappedException)
        {
            // The service does not exist, so its account does not resolve; the SID is still its.
            return computed;
        }
    }

    private static (NamespaceGrant? State, string? Note) EncryptionAccess(RipcordService service)
    {
        try
        {
            (NamespaceGrant state, string? why) = NamespaceAcl.Read(Sddl(ReadEncryptionSd()), SidOf(service));
            return (state, why);
        }
        catch (CimException exception) when (exception.NativeErrorCode
            is NativeErrorCode.InvalidNamespace or NativeErrorCode.NotFound or NativeErrorCode.InvalidClass)
        {
            return (null, "BitLocker is not installed on this host");
        }
        catch (Exception exception) when (exception is CimException or InvalidOperationException)
        {
            return (null, $"the BitLocker namespace's access list could not be read: {exception.Message}");
        }
    }

    /// One ACE added or taken back, as the Domain edited it; read back after writing, and the
    /// original bytes restored if what was written is not what was meant.
    private static void EditEncryptionNamespace(RipcordService service, bool grant)
    {
        string sid = SidOf(service);
        byte[] original = ReadEncryptionSd();
        string before = Sddl(original);

        NamespaceEdit edit = grant ? NamespaceAcl.WithGrant(before, sid) : NamespaceAcl.WithoutGrant(before, sid);

        if (edit.Sddl is not { } after)
        {
            throw new InvalidOperationException(
                $"the BitLocker namespace was left as it was: {edit.Refusal}");
        }

        if (after == before)
        {
            return;
        }

        RawSecurityDescriptor descriptor = new(after);
        byte[] written = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(written, 0);
        WriteEncryptionSd(written);

        if (NamespaceAcl.Read(Sddl(ReadEncryptionSd()), sid).State
            != (grant ? NamespaceGrant.Granted : NamespaceGrant.Missing))
        {
            WriteEncryptionSd(original);
            throw new InvalidOperationException(
                "the BitLocker namespace did not read back as written, and was restored");
        }
    }

    private static string Sddl(byte[] descriptor) =>
        new RawSecurityDescriptor(descriptor, 0).GetSddlForm(
            AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access);

    private static byte[] ReadEncryptionSd()
    {
        using CimSession session = CimSession.Create(computerName: null);
        using CimOperationOptions options = new() { Timeout = CimTimeout };
        using CimMethodResult result = session.InvokeMethod(
            EncryptionNamespace, "__SystemSecurity", "GetSD", new CimMethodParametersCollection(), options);

        return ReturnValue(result) == 0 && result.OutParameters["SD"]?.Value is byte[] descriptor
            ? descriptor
            : throw new InvalidOperationException($"GetSD returned {ReturnValue(result)}");
    }

    private static void WriteEncryptionSd(byte[] descriptor)
    {
        using CimSession session = CimSession.Create(computerName: null);
        using CimOperationOptions options = new() { Timeout = CimTimeout };
        using CimMethodParametersCollection parameters = new()
        {
            CimMethodParameter.Create("SD", descriptor, CimType.UInt8Array, CimFlags.In),
        };
        using CimMethodResult result = session.InvokeMethod(
            EncryptionNamespace, "__SystemSecurity", "SetSD", parameters, options);

        if (ReturnValue(result) != 0)
        {
            throw new InvalidOperationException($"SetSD returned {ReturnValue(result)}");
        }
    }

    private static long ReturnValue(CimMethodResult result) =>
        Convert.ToInt64(result.ReturnValue?.Value ?? -1, CultureInfo.InvariantCulture);

    private static string LogsOf(DeploymentStep change, DesiredDeployment desired) =>
        change.Subject == RipcordService.Publisher ? desired.PublisherLogsFolder : desired.LogsFolder;

    /// Read only, on the key file only: the account signs with the key, it never replaces it.
    private static IEnumerable<(string File, string Arguments)> KeyCommands(
        DeploymentAction action, DesiredDeployment desired)
    {
        string key = KeyFile(desired)
            ?? throw new InvalidOperationException(
                $"the private key of certificate {desired.CertificateThumbprint} was not found "
                + @"in LocalMachine\My");

        return action == DeploymentAction.GrantKeyAccess
            ? [("icacls", $"\"{key}\" /grant \"{RipcordService.Listener.Account}\":(R)")]
            : [("icacls", $"\"{key}\" /remove \"{RipcordService.Listener.Account}\"")];
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
            Run("icacls", $"\"{snapshotFolder}\"").Output, RipcordService.Listener.Account);

    /// Modify, not merely write: pruning an old log deletes it.
    private static bool LogsWritable(string logsFolder) =>
        Directory.Exists(logsFolder)
        && AccessControl.GrantsModify(
            Run("icacls", $"\"{logsFolder}\"").Output, RipcordService.Listener.Account);

    private static bool EventSourceRegistered()
    {
        using Microsoft.Win32.RegistryKey? key =
            Microsoft.Win32.Registry.LocalMachine.OpenSubKey(EventSourceKey);

        return key is not null;
    }

    /// From the registry, not from `sc qc`, whose labels are localised.
    private static string? ServiceImagePath(RipcordService which)
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{which.Name}");

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
