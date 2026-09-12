using System.Diagnostics;
using System.Globalization;
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

        (int serviceCode, string serviceOutput) = Run("sc.exe", $"qc {DeploymentPlan.ServiceName}");
        (int ruleCode, string ruleOutput) = Run(
            "netsh",
            $"advfirewall firewall show rule name=\"{DeploymentPlan.FirewallRuleName}\" verbose");

        bool serviceInstalled = serviceCode == 0;
        bool ruleInstalled = ruleCode == 0;

        return new ObservedDeployment(
            serviceInstalled,
            serviceInstalled ? ValueAfter(serviceOutput, "BINARY_PATH_NAME") : null,
            ruleInstalled,
            ruleInstalled ? PortIn(ruleOutput) : null,
            ruleInstalled ? ValueAfter(ruleOutput, "RemoteIP") : null,
            SnapshotReadable(desired.SnapshotPath));
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
    private static bool SnapshotReadable(string snapshotPath) =>
        File.Exists(snapshotPath)
        && Run("icacls", $"\"{snapshotPath}\"").Output
            .Contains(DeploymentPlan.ServiceAccount, StringComparison.OrdinalIgnoreCase);

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
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();

        // Explicit timeout, like every other outward call: a wedged sc.exe must not wedge the
        // operator's console during an incident.
        if (!process.WaitForExit(CommandTimeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{file} {arguments}' did not finish in {CommandTimeout}");
        }

        return (process.ExitCode, output);
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
