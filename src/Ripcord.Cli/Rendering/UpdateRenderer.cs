using System.Text;
using Ripcord.Application.Updates;
using Ripcord.Domain.Updates;

namespace Ripcord.Cli.Rendering;

/// What the update would do, then what it did. Same fixed width and same plain ASCII as every
/// other renderer: this one is read on the same console, often straight after `check`.
public static class UpdateRenderer
{
    public static string Render(
        UpdatePlan plan,
        string runningVersion,
        string? availableVersion,
        Palette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        StringBuilder output = new();

        output.AppendLine(Ink.Bold(Layout.Pad("RIPCORD UPDATE", Layout.Width)));
        output.AppendLine();
        output.AppendLine($"  Running    {runningVersion}");
        output.AppendLine($"  Available  {availableVersion ?? "not established"}");
        output.AppendLine();

        if (plan.Halt is { } halt)
        {
            AppendWrapped(output, "REFUSED", halt, refused: true);
            return Layout.Rendered(output, palette);
        }

        if (!plan.ChangesAnything)
        {
            AppendWrapped(output, "", plan.Status.Explanation);
            return Layout.Rendered(output, palette);
        }

        foreach (UpdateStep step in plan.Steps)
        {
            output.AppendLine($"  {step.Number}. {step.Description}");
            output.AppendLine($"     because {step.Reason}");
        }

        // Above the confirmation, never below it: a consequence read after the decision is a
        // consequence nobody weighed.
        if (plan.Warnings.Count > 0)
        {
            output.AppendLine();

            foreach (string warning in plan.Warnings)
            {
                AppendWrapped(output, "WARNING", warning);
            }
        }

        return Layout.Rendered(output, palette);
    }

    /// Written as the step starts, so the last line on screen is the one still running.
    public static string RenderStarting(UpdateStep step, int total)
    {
        ArgumentNullException.ThrowIfNull(step);

        return $"  {step.Number}/{total}  {step.Description}...";
    }

    /// Indented under the step's description; later marks follow on the same line.
    public static string RenderMark(string mark, bool first) =>
        first ? $"       {mark}" : $" {mark}";

    /// The steps were written as they started; this is only how it ended.
    public static string RenderResult(
        UpdateResult result, string version, Palette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        StringBuilder output = new();

        if (result.Failed is null)
        {
            output.AppendLine();

            // The running process is still the old image — Windows holds it open — so the
            // version anybody checks right now is the old one. Saying it beats a support call.
            AppendWrapped(
                output,
                "",
                $"{version} is installed. This process is still running the previous build; "
                + "restart the ripcord service, or the next command run picks it up.");

            return Layout.Rendered(output, palette);
        }

        output.AppendLine();
        AppendWrapped(output, "FAILED", $"{result.Failed.Description}: {result.FailureMessage}");

        if (result.Rollback is { Succeeded: true })
        {
            AppendWrapped(output, "", "The running binary was put back; nothing was changed.");
        }

        if (result.ManualRecovery is { } recovery)
        {
            output.AppendLine();
            AppendWrapped(output, "DO THIS NOW", recovery);
        }

        return Layout.Rendered(output, palette);
    }

    /// The label is coloured after the prefix has been measured, never before: wrapping a
    /// marker-bearing label made `prefix.Length` two characters too long, which narrowed the
    /// wrap and widened the hanging indent of every continuation line.
    private static void AppendWrapped(
        StringBuilder output, string label, string text, bool refused = false)
    {
        string prefix = label.Length > 0 ? $"  {label}: " : "  ";
        string indent = new(' ', prefix.Length);
        bool first = true;

        foreach (string line in Layout.Wrap(text, Layout.Width - prefix.Length))
        {
            output.AppendLine((first ? Tint(prefix, refused) : indent) + line);
            first = false;
        }
    }

    private static string Tint(string prefix, bool refused) =>
        refused ? Ink.Red(prefix) : prefix;
}
