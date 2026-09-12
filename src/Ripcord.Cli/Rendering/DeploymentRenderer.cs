using System.Globalization;
using System.Text;
using Ripcord.Domain.Deployment;

namespace Ripcord.Cli.Rendering;

/// The plan, written for someone about to authorise it. Same fixed width and no colour as the
/// status page, for the same reason.
public static class DeploymentRenderer
{
    public static string Render(DeploymentPlan plan, DesiredDeployment desired, bool removing)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(desired);

        StringBuilder output = new();

        output.AppendLine(removing
            ? "RIPCORD LISTENER REMOVAL"
            : "RIPCORD LISTENER DEPLOYMENT");
        output.AppendLine();

        if (!plan.ChangesAnything)
        {
            output.AppendLine(removing
                ? "  Nothing to remove: this host has no listener deployed."
                : "  Nothing to do: this host already matches the configuration.");
            return output.ToString();
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

        return output.ToString();
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
            output.AppendLine("  The host is in an intermediate state. Re-run the command once");
            output.AppendLine("  the cause is fixed: it resumes from where it stopped.");
        }

        return output.ToString();
    }
}
