using System.Text;
using Ripcord.Application.Failover;
using Ripcord.Domain.Failover;

namespace Ripcord.Cli.Rendering;

/// What the fence changed, or would change, on the host that has just come back.
///
/// The prior setting is the column that matters: `reprotect` restores it, and a host that
/// comes home with every VM set to never start is a second outage waiting for the next reboot.
/// Where it could not be read the row says so rather than leaving a blank somebody reads as
/// "nothing to put back".
public static class FenceRenderer
{
    private const int VmColumn = 20;
    private const int WasColumn = 18;

    public static string Render(FenceOutcome outcome, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        bool dryRun = outcome.Fenced.Count == 0 && outcome.Plan?.HasWork == true;

        StringBuilder output = new();

        output.AppendLine(Layout.Banner(
            dryRun ? "RIPCORD FENCE (DRY RUN)" : "RIPCORD FENCE", now));
        output.AppendLine($"ripcord {BuildInfo.VersionWithCommit}");
        output.AppendLine();

        if (outcome.Scope is { } scope)
        {
            AppendBlock(output, "SCOPE", scope);
            output.AppendLine();
        }

        if (outcome.Plan is not { } plan)
        {
            AppendBlock(output, "NOT FENCED", outcome.FailureMessage ?? "nothing was read");
            return Layout.Rendered(output);
        }

        if (plan.Halt is { } halt)
        {
            AppendBlock(output, "HALTED", halt);
            return Layout.Rendered(output);
        }

        AppendRows(output, outcome, plan, dryRun);

        if (plan.AlreadyFenced.Count > 0)
        {
            output.AppendLine();
            AppendBlock(
                output,
                "ALREADY FENCED",
                string.Join(", ", plan.AlreadyFenced));
        }

        // Not off is not fenced. A saved or paused copy resumes into a second live claimant,
        // and a start action of Nothing does not stop somebody resuming it by hand.
        if (plan.NotConfirmedOff.Count > 0)
        {
            output.AppendLine();
            AppendBlock(
                output,
                "NOT CONFIRMED OFF",
                "these are fenced against the next boot but are not switched off, so they can "
                    + "still be resumed into a second live copy: "
                    + string.Join(", ", plan.NotConfirmedOff));
        }

        if (plan.Absent.Count > 0)
        {
            output.AppendLine();
            AppendBlock(
                output,
                "NOT ON THIS HOST",
                string.Join(", ", plan.Absent));
        }

        return Layout.Rendered(output);
    }

    private static void AppendRows(
        StringBuilder output, FenceOutcome outcome, FencePlan plan, bool dryRun)
    {
        if (!plan.HasWork)
        {
            output.AppendLine("  Nothing to fence: no VM here would start itself.");
            return;
        }

        output.AppendLine(
            "  " + Layout.Pad("VM", VmColumn) + Layout.Pad("WAS", WasColumn) + "OUTCOME");

        output.AppendLine("  " + Layout.Line(VmColumn + WasColumn + 30));

        foreach (FenceAction action in plan.ToFence)
        {
            FencedVm? done = outcome.Fenced.FirstOrDefault(vm =>
                string.Equals(vm.VmName, action.VmName, StringComparison.OrdinalIgnoreCase));

            output.AppendLine(
                "  "
                + Layout.Pad(Layout.Truncate(action.VmName, VmColumn - 1), VmColumn)
                + Layout.Pad(
                    action.PreviousIsKnown ? action.Previous.ToString() : "not read", WasColumn)
                + Outcome(done, dryRun));

            if (done?.Failure is { } failure)
            {
                foreach (string line in Layout.Wrap(failure, 60))
                {
                    output.AppendLine("      " + line);
                }
            }

            if (!action.PreviousIsKnown)
            {
                output.AppendLine(
                    "      the previous setting could not be read, so reprotect cannot put it");
                output.AppendLine("      back — note it by hand if you know what it was");
            }
        }
    }

    private static string Outcome(FencedVm? done, bool dryRun) =>
        dryRun ? "would fence" : done is null ? "not attempted" : done.Succeeded ? "fenced" : "FAILED";

    private static void AppendBlock(StringBuilder output, string title, string body)
    {
        output.AppendLine($"  {title}");

        foreach (string line in Layout.Wrap(body, 68))
        {
            output.AppendLine("    " + line);
        }
    }
}
