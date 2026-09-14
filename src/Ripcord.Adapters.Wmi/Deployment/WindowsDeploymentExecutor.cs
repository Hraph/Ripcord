using System.Diagnostics;
using System.Globalization;
using Microsoft.Management.Infrastructure;
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

    public ObservedDeployment Observe(DesiredDeployment desired)
    {
        ArgumentNullException.ThrowIfNull(desired);

        // The service comes from the registry, not from `sc qc`: that command's labels are
        // localised, and on a French Windows every run would report a different binary path
        // and reconfigure the service pointlessly.
        string? imagePath = ServiceImagePath();

        (int ruleCode, string ruleOutput) = Run(
            "netsh",
            $"advfirewall firewall show rule name=\"{DeploymentPlan.FirewallRuleName}\" verbose");

        bool ruleInstalled = ruleCode == 0;

        return new ObservedDeployment(
            imagePath is not null,
            BinaryIn(imagePath),
            ruleInstalled,
            ruleInstalled ? PortIn(ruleOutput) : null,
            ruleInstalled ? ValueAfter(ruleOutput, "RemoteIP") : null,
            SnapshotReadable(desired.SnapshotPath),
            imagePath is not null && ServiceIsStarted());
    }

    public void Apply(DeploymentStep change, DesiredDeployment desired)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(desired);

        foreach ((string file, string arguments) in CommandsFor(change.Action, desired))
        {
            (int code, string output) = Run(file, arguments);

            if (code != 0)
            {
                throw new InvalidOperationException(
                    $"'{file} {arguments}' exited with {code}: {output.Trim()}");
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
            ("icacls", $"\"{desired.SnapshotPath}\" /grant \"{DeploymentPlan.ServiceAccount}\":(R)"),
        ],

        _ =>
        [
            ("icacls", $"\"{desired.SnapshotPath}\" /remove \"{DeploymentPlan.ServiceAccount}\""),
        ],
    };

    private static string FirewallRule(string verb, DesiredDeployment desired) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"advfirewall firewall {verb} rule name=\"{DeploymentPlan.FirewallRuleName}\" "
            + $"dir=in action=allow protocol=TCP localport={desired.Port} "
            + $"remoteip={desired.PeerAddress}");

    /// The service account's access is what matters, and only Windows can answer that. The
    /// file being absent is not the same as being unreadable, but it needs the same step.
    ///
    /// What the answer means is AccessControl's: an entry denying the account also names it,
    /// and a substring search would read that as access the service does not have.
    private static bool SnapshotReadable(string snapshotPath) =>
        File.Exists(snapshotPath)
        && AccessControl.GrantsRead(
            Run("icacls", $"\"{snapshotPath}\"").Output, DeploymentPlan.ServiceAccount);

    /// Whether the service is running.
    ///
    /// `Win32_Service.Started` — a boolean — rather than `sc query`'s state, for the same
    /// reason the image path comes from the registry: `sc` prints its labels and its state
    /// words in the host's language, and a French Windows would answer something this code
    /// would read as "not running" for ever.
    ///
    /// Unreadable is reported as not running. The consequence is one redundant `sc start` on a
    /// service that is already up, which does nothing; the opposite default would leave a
    /// stopped listener alone and call the deployment correct.
    private static bool ServiceIsStarted()
    {
        try
        {
            using CimSession session = CimSession.Create(computerName: null);

            CimInstance? service = session
                .QueryInstances(
                    @"root\cimv2",
                    "WQL",
                    $"SELECT Started FROM Win32_Service WHERE Name = '{DeploymentPlan.ServiceName}'")
                .FirstOrDefault();

            using (service)
            {
                return service?.CimInstanceProperties["Started"]?.Value is true;
            }
        }
        catch (CimException)
        {
            return false;
        }
    }

    /// `netsh`'s field labels are localised too, so an unparseable rule is reported as not
    /// matching rather than as matching. That costs one redundant rule rewrite per run on a
    /// non-English host; reporting it as correct could leave a rule open to the wrong address.
    /// See V23.
    private static string? ServiceImagePath()
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            $@"SYSTEM\CurrentControlSet\Services\{DeploymentPlan.ServiceName}");

        return key?.GetValue("ImagePath") as string;
    }

    /// The registry holds `"<path>" serve`; the plan compares binaries.
    private static string? BinaryIn(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        string trimmed = imagePath.Trim();

        return trimmed.StartsWith('"') && trimmed.IndexOf('"', 1) is int end and > 0
            ? trimmed[1..end]
            : trimmed.Split(' ')[0];
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
