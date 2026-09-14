using System.Globalization;
using System.Text;
using Ripcord.Domain.Deployment;

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
        string? heading = null)
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
            AppendObserved(output, observed);
        }

        if (!plan.ChangesAnything)
        {
            output.AppendLine(removing
                ? "  Nothing to remove: this host has no listener deployed."
                : "  Nothing to do: this host already matches the configuration.");
            return Layout.Rendered(output);
        }

        int number = 1;

        foreach (DeploymentStep step in plan.Steps)
        {
            output.AppendLine(string.Create(
                CultureInfo.InvariantCulture, $"  {number}. {step.Description}"));
            output.AppendLine($"     because {step.Reason}");
            output.AppendLine();
            number++;
        }

        return Layout.Rendered(output);
    }

    /// The service on its own: what is there, and one line about what is not.
    ///
    /// `ripcord service` is read-only and is the question an operator asks first. It says what
    /// would change only by naming the command that would show it — a plan printed by a
    /// command that changes nothing reads like a command that is about to.
    public static string RenderState(
        ObservedDeployment observed, DesiredDeployment desired, DeploymentPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(desired);

        StringBuilder output = new();

        output.AppendLine("RIPCORD LISTENER");
        output.AppendLine();

        AppendObserved(output, observed);

        output.AppendLine(plan is null || !plan.ChangesAnything
            ? "  It matches the configuration."
            : $"  {plan.Steps.Count} step(s) would change it: ripcord service install --dry-run");

        return Layout.Rendered(output);
    }

    /// What is on the host now, before what would change about it.
    ///
    /// Installed and running are two facts, not one: a service that is registered and stopped
    /// serves nothing, and the other host then reports this pair offline — which reads as a
    /// network fault rather than as a service somebody has to start. The command line is
    /// printed with it because a second copy of the binary in another directory is how a pair
    /// ends up running two versions.
    private static void AppendObserved(StringBuilder output, ObservedDeployment observed)
    {
        output.AppendLine("  ON THIS HOST");

        output.AppendLine(observed.ServiceInstalled
            ? $"    service    {(observed.ServiceRunning ? "running" : "STOPPED")}"
                + $"     {observed.ServiceBinaryPath} serve"
            : "    service    not installed");

        output.AppendLine(observed.FirewallRuleInstalled
            ? $"    firewall   inbound TCP {observed.FirewallPort} "
                + $"from {observed.FirewallRemoteAddress}"
            : "    firewall   no rule");

        output.AppendLine(observed.SnapshotReadableByService
            ? $"    snapshot   readable by {DeploymentPlan.ServiceAccount}"
            : $"    snapshot   not readable by {DeploymentPlan.ServiceAccount}");

        output.AppendLine();
    }

    /// What was actually done, including the step that failed. A half-applied plan has to be
    /// legible: the operator needs to know where the host stopped.
    public static string RenderResult(
        IReadOnlyList<DeploymentStep> applied, DeploymentStep? failed, string? failureMessage)
    {
        ArgumentNullException.ThrowIfNull(applied);

        StringBuilder output = new();

        foreach (DeploymentStep step in applied)
        {
            output.AppendLine($"  done: {step.Description}");
        }

        if (failed is not null)
        {
            output.AppendLine($"  FAILED: {failed.Description}");
            output.AppendLine($"          {failureMessage}");
            output.AppendLine();

            // Failing on the first step changed nothing, and telling an operator the host is
            // half-deployed when it is untouched sends them looking for damage that is not
            // there.
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

        return Layout.Rendered(output);
    }
}
