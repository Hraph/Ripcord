using System.Text;
using Ripcord.Domain.Updates;

namespace Ripcord.Cli.Rendering;

/// What `ripcord rollback` is about to do, in the shape the update plan uses — the two are
/// read one after the other often enough that they have to line up.
public static class RollbackRenderer
{
    public static string Render(
        RollbackPlan plan,
        string runningVersion,
        DateTimeOffset? setAsideAt,
        Palette? palette = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        StringBuilder output = new();

        output.AppendLine(Ink.Bold(Layout.Pad("RIPCORD ROLLBACK", Layout.Width)));
        output.AppendLine();
        output.AppendLine($"  Running    {runningVersion}");
        output.AppendLine($"  Set aside  {Aside(plan, setAsideAt)}");
        output.AppendLine();

        if (plan.Halt is { } halt)
        {
            AppendWrapped(output, Ink.Red("  REFUSED: "), halt);
            return Layout.Rendered(output, palette);
        }

        foreach (RollbackStep step in plan.Steps)
        {
            output.AppendLine($"  {step.Number}  {step.Description}");
            output.AppendLine($"     {Ink.Faint(step.Reason)}");
        }

        output.AppendLine();

        foreach (string warning in plan.Warnings)
        {
            AppendWrapped(output, Ink.Amber("  WARNING: "), warning);
        }

        return Layout.Rendered(output, palette);
    }

    /// The version and when it was kept, or the plain fact that neither could be read. A blank
    /// here would read as "nothing set aside", which is a different answer entirely.
    private static string Aside(RollbackPlan plan, DateTimeOffset? setAsideAt)
    {
        if (plan is { Halt: not null })
        {
            return "nothing";
        }

        string version = plan.PreviousVersion ?? "a version that could not be read";

        return setAsideAt is { } kept
            ? $"{version}  (kept on {Layout.Date(kept)})"
            : version;
    }

    /// The label is measured plain and coloured after: a marker inside `prefix.Length` would
    /// narrow the wrap and widen the hanging indent of every line after the first.
    private static void AppendWrapped(StringBuilder output, string label, string text)
    {
        string plain = Palette.None.Apply(label);
        string indent = new(' ', plain.Length);
        bool first = true;

        foreach (string line in Layout.Wrap(text, Layout.Width - plain.Length))
        {
            output.AppendLine((first ? label : indent) + line);
            first = false;
        }
    }
}
