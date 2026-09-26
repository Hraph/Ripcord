using System.Globalization;
using System.Text;
using Ripcord.Application.Deployment;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Inventory;

namespace Ripcord.Cli.Rendering;

/// The plan, written for someone about to authorise it. Same fixed width and no colour as the
/// status page, for the same reason.
public static class DeploymentRenderer
{
    public static string Render(
        DeploymentPlan plan,
        DesiredDeployment desired,
        bool removing,
        ObservedDeployment? observed = null,

        /// What this run is, when it is neither a deployment nor a removal. A restart under a
        /// "DEPLOYMENT" banner is the kind of small lie that costs a second of doubt on the
        /// one screen that is read under pressure.
        string? heading = null,
        Palette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(desired);

        StringBuilder output = new();

        output.AppendLine(heading ?? (removing
            ? "RIPCORD LISTENER REMOVAL"
            : "RIPCORD LISTENER DEPLOYMENT"));
        output.AppendLine();

        if (observed is not null)
        {
            AppendObserved(output, observed, desired);
        }

        if (plan.IsBlocked)
        {
            AppendBlocked(output, plan);
            return Layout.Rendered(output, palette);
        }

        if (!plan.ChangesAnything)
        {
            output.AppendLine(removing
                ? "  Nothing to remove: this host has no listener deployed."
                : "  Nothing to do: this host already matches the configuration.");
            return Layout.Rendered(output, palette);
        }

        int number = 1;

        foreach (DeploymentStep step in plan.Steps)
        {
            AppendWrapped(
                output,
                string.Create(CultureInfo.InvariantCulture, $"  {number}. "),
                "     ",
                step.Description);
            AppendWrapped(output, "     because ", "             ", step.Reason);
            output.AppendLine();
            number++;
        }

        return Layout.Rendered(output, palette);
    }

    /// The service on its own: what is there, and when it is not running, why.
    ///
    /// `ripcord service` is read-only and is the question an operator asks first. It says what
    /// would change only by naming the command that would show it — a plan printed by a
    /// command that changes nothing reads like a command that is about to.
    public static string RenderState(ServiceReport report, Palette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        StringBuilder output = new();

        output.AppendLine(Ink.Bold("RIPCORD LISTENER"));
        output.AppendLine();
        output.AppendLine("  ON THIS HOST");

        ObservedService service = report.Service;
        DeploymentOutcome deployment = report.Deployment;

        AppendService(output, service);
        AppendBuild(output, report.Build);

        if (deployment is { Observed: { } observed, Desired: { } desired })
        {
            AppendAccess(output, observed, desired, report);
        }
        else
        {
            AppendWrapped(
                output,
                "    ",
                "    ",
                deployment.FailureMessage is null
                    ? "Firewall, snapshot and logs access are not shown: the configuration "
                        + "cannot be used (below)."
                    : "Firewall, snapshot and logs access could not be read (below).");
        }

        AppendCertificates(output, report.Certificates, deployment.Desired?.CertificateThumbprint);
        AppendLog(output, report, shownFolder: deployment.Desired?.LogsFolder);
        output.AppendLine();

        if (service.Installed && report.Verdict.Why is { } why)
        {
            // Only a stopped service is "not running": an unknown state is never guessed as one.
            output.AppendLine(service.State == ServiceRunState.Stopped
                ? "  Why it is not running:"
                : "  Note:");
            AppendWrapped(output, "    ", "    ", why);

            // Commands on lines of their own, never wrapped: they are typed from the screen.
            if (report.Verdict.Next.Count > 0)
            {
                output.AppendLine("  Then run:");

                foreach (string next in report.Verdict.Next)
                {
                    output.AppendLine($"    {next}");
                }
            }

            output.AppendLine();
        }

        if (deployment.Plan is { IsBlocked: true } blocked)
        {
            AppendBlocked(output, blocked);
        }
        else if (deployment.Plan is { } plan)
        {
            output.AppendLine(plan.ChangesAnything
                ? $"  {plan.Steps.Count} step(s) would change it: ripcord service install --dry-run"
                : "  It matches the configuration.");
        }

        if (report.Snapshot is SnapshotAge.Missing or SnapshotAge.Stale)
        {
            output.AppendLine("  The peer is served no current snapshot until this runs:");
            output.AppendLine("    ripcord status");
        }

        return Layout.Rendered(output, palette);
    }

    /// What Windows reports, raw: the command it runs is printed as registered, because a
    /// second copy of the binary in another directory is how a pair ends up running two
    /// versions.
    private static void AppendService(StringBuilder output, ObservedService service)
    {
        if (!service.Installed)
        {
            output.AppendLine("    service    not installed");
            return;
        }

        string state = service.State switch
        {
            ServiceRunState.Running => "running",
            ServiceRunState.Stopped => "STOPPED",
            ServiceRunState.StartPending => "starting",
            ServiceRunState.StopPending => "stopping",
            ServiceRunState.Paused => "PAUSED",
            _ => "state unknown",
        };

        output.AppendLine(service.StartMode is { } mode
            ? $"    service    {Layout.Pad(state, 13)}start mode {mode}"
            : $"    service    {state}");

        if (service.CommandLine is { } commandLine)
        {
            AppendWrapped(output, "    command    ", "               ", commandLine);
        }

        if (service.State != ServiceRunState.Running && service.LastExit is { } exit)
        {
            AppendWrapped(
                output, "    last exit  ", "               ", ServiceDiagnosis.WindowsMeaning(exit));
        }
    }

    /// The thumbprint to paste on both hosts, shown even when `ripcord.yaml` does not load:
    /// a placeholder in it is exactly why it does not.
    private static void AppendCertificates(
        StringBuilder output, HostCertificates? certificates, string? configured)
    {
        if (certificates is null)
        {
            return;
        }

        output.AppendLine();
        output.AppendLine("  THIS HOST'S CERTIFICATE");

        if (certificates.Unreadable is { } reason)
        {
            AppendWrapped(output, "    ", "    ", $"LocalMachine\\My could not be read: {reason}");
            return;
        }

        if (certificates.Usable.Count == 0)
        {
            AppendWrapped(
                output,
                "    ",
                "    ",
                $"NONE: no certificate for {certificates.Subject} with a private key in "
                    + "LocalMachine\\My that has not expired.");
            return;
        }

        foreach (CertificateFact certificate in certificates.Usable)
        {
            bool inUse = string.Equals(certificate.Thumbprint, configured, StringComparison.OrdinalIgnoreCase);

            output.AppendLine($"    {certificate.Thumbprint}{(inUse ? "  in ripcord.yaml" : "")}");
            AppendWrapped(
                output,
                "      ",
                "      ",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{certificate.CommonName}, expires {certificate.NotAfter:yyyy-MM-dd}"));
        }

        // A renewal leaves ripcord.yaml on the old thumbprint, which the listener then cannot use.
        if (configured is not null && !certificates.Lists(configured))
        {
            output.AppendLine("    None of these is the one in ripcord.yaml.");
        }

        // The configured one if it is listed, else the latest: the line is what gets pasted.
        CertificateFact shown = certificates.Usable.FirstOrDefault(certificate =>
                string.Equals(certificate.Thumbprint, configured, StringComparison.OrdinalIgnoreCase))
            ?? certificates.Usable[0];

        output.AppendLine("    On the other host, run:");
        output.AppendLine(
            $"      ripcord pair {ListenerPairing.Key(certificates.MachineName, shown.Thumbprint)}");
    }

    /// The build the process runs, which after `ripcord update` is not the one on disk.
    private static void AppendBuild(StringBuilder output, RunningBuild? build)
    {
        switch (build)
        {
            case { Build: { } running, Outdated: true }:
                output.AppendLine($"    version    {running}");
                output.AppendLine("               NOT the build of this ripcord.exe: restart it");
                break;
            case { Build: { } running }:
                output.AppendLine($"    version    {running}");
                break;
            case { Unknown: { } reason }:
                AppendWrapped(output, "    version    ", "               ", $"unknown, {reason}");
                break;
        }
    }

    /// Which file was read, and its last lines. The date is dropped from each line: the file
    /// is one day's, and the columns are better spent on what the line says.
    private static void AppendLog(StringBuilder output, ServiceReport report, string? shownFolder)
    {
        // The file name alone when the logs row above already names its folder: a full path
        // under Program Files does not fit on one line.
        bool folderShown = string.Equals(
            shownFolder, report.LogsFolder, StringComparison.OrdinalIgnoreCase);

        if (report.Log is not { } log)
        {
            output.AppendLine(folderShown
                ? "    log        none today or yesterday"
                : "    log        none today or yesterday in");

            if (!folderShown)
            {
                output.AppendLine($"               {report.LogsFolder}");
            }

            return;
        }

        output.AppendLine($"    log        {LogFileName(log.Path)}");

        if (!folderShown)
        {
            output.AppendLine($"               in {report.LogsFolder}");
        }

        if (log.Unreadable is { } reason)
        {
            AppendWrapped(output, "               NOT readable: ", "               ", reason);
            return;
        }

        IReadOnlyList<string> shown = [.. log.Lines.TakeLast(ServiceLog.ShownLines)];

        if (shown.Count == 0)
        {
            output.AppendLine("               empty");
            return;
        }

        output.AppendLine();
        output.AppendLine(string.Create(
            CultureInfo.InvariantCulture, $"  LAST {shown.Count} LINES OF THE LOG"));

        foreach (string line in shown)
        {
            output.AppendLine("    " + Layout.Truncate(WithoutDate(line), Layout.Width - 4));
        }
    }

    private static string LogFileName(string path) =>
        path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    private static string WithoutDate(string line) =>
        line.Length > 11 && line[10] == ' '
            && DateOnly.TryParseExact(
                line[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? line[11..]
            : line;

    /// What is on the host now, before what would change about it.
    ///
    /// Installed and running are two facts, not one: a service that is registered and stopped
    /// serves nothing, and the other host then reports this pair offline — which reads as a
    /// network fault rather than as a service somebody has to start.
    private static void AppendObserved(
        StringBuilder output, ObservedDeployment observed, DesiredDeployment desired)
    {
        output.AppendLine("  ON THIS HOST");

        output.AppendLine(observed.ServiceInstalled
            ? $"    service    {(observed.ServiceRunning ? "running" : "STOPPED")}"
                + $"     {observed.ServiceBinaryPath} serve"
            : "    service    not installed");

        AppendAccess(output, observed, desired, report: null);
        output.AppendLine();
    }

    private static void AppendAccess(
        StringBuilder output,
        ObservedDeployment observed,
        DesiredDeployment desired,
        ServiceReport? report)
    {
        output.AppendLine(observed.FirewallRuleInstalled
            ? $"    firewall   inbound TCP {observed.FirewallPort} "
                + $"from {observed.FirewallRemoteAddress}"
            : "    firewall   no rule");

        // Named and explained: an operator granting access to a file has to know what it is.
        AppendWrapped(output, "    snapshot   ", "               ", desired.SnapshotPath);
        output.AppendLine("               written by 'ripcord status', served to the peer");

        switch (report?.Snapshot)
        {
            case SnapshotAge.Missing:
                output.AppendLine("               NOT written yet");
                break;
            case SnapshotAge.Stale:
                output.AppendLine(
                    $"               written {Layout.Duration(report.SnapshotWrittenAgo)} ago: STALE");
                break;
            case SnapshotAge.Fresh:
                output.AppendLine($"               written {Layout.Duration(report.SnapshotWrittenAgo)} ago");
                break;
        }
        output.AppendLine(observed.SnapshotReadableByService
            ? $"               readable by {RipcordService.Listener.Account}"
            : $"               NOT readable by {RipcordService.Listener.Account}");

        // The key the handshake signs with. Machine keys are readable by SYSTEM and
        // Administrators only, so the service account needs its own grant.
        if (desired.CertificateThumbprint is { } thumbprint)
        {
            output.AppendLine(observed.KeyReadableByService switch
            {
                true => $"    key        readable by {RipcordService.Listener.Account}",
                false => $"    key        NOT readable by {RipcordService.Listener.Account}",
                null => "    key        NOT found in LocalMachine\\My for certificate",
            });
            output.AppendLine($"               {thumbprint}");
        }

        // The folder on its own line: an install under Program Files overflows 75 columns.
        output.AppendLine(observed.LogsWritableByService
            ? $"    logs       writable by {RipcordService.Listener.Account}"
            : $"    logs       NOT writable by {RipcordService.Listener.Account}");
        output.AppendLine($"               {desired.LogsFolder}");
    }

    private static void AppendBlocked(StringBuilder output, DeploymentPlan plan)
    {
        output.AppendLine("  Cannot be installed as configured:");
        AppendWrapped(output, "    ", "    ", plan.BlockedBy!);
    }

    /// Descriptions and reasons carry paths, and a path under Program Files overflows 75
    /// columns on its own.
    private static void AppendWrapped(
        StringBuilder output, string first, string rest, string text)
    {
        string prefix = first;

        foreach (string line in Layout.Wrap(text, Layout.Width - first.Length))
        {
            output.AppendLine(prefix + line);
            prefix = rest;
        }
    }

    /// What was actually done, including the step that failed. A half-applied plan has to be
    /// legible: the operator needs to know where the host stopped.
    public static string RenderResult(
        IReadOnlyList<DeploymentStep> applied,
        DeploymentStep? failed,
        string? failureMessage,
        Palette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(applied);

        StringBuilder output = new();

        foreach (DeploymentStep step in applied)
        {
            AppendWrapped(output, "  done: ", "        ", step.Description);
        }

        if (failed is not null)
        {
            string prefix = "  FAILED: ";

            foreach (string line in Layout.Wrap(failed.Description, Layout.Width - prefix.Length))
            {
                output.AppendLine(Ink.Red(prefix + line));
                prefix = "          ";
            }

            AppendWrapped(output, "          ", "          ", failureMessage ?? "");
            output.AppendLine();

            // Failing on the first step changed nothing, and telling an operator the host is
            // half-deployed when it is untouched sends them looking for damage that is not
            // there.
            // The step came back but the service did not stay up, or did not answer: the reason
            // is in its log or the event log, which `ripcord service` points at.
            if (DeploymentPlan.StartsTheService(failed.Action))
            {
                output.AppendLine("  Run 'ripcord service' to see why it did not start.");
                output.AppendLine();
            }

            if (applied.Count == 0)
            {
                output.AppendLine(
                    "  Nothing was changed. Re-run the command once the cause is fixed.");
            }
            else
            {
                output.AppendLine("  The host is in an intermediate state. Re-run the command once");
                output.AppendLine("  the cause is fixed: it resumes from where it stopped.");
            }
        }

        return Layout.Rendered(output, palette);
    }
}
