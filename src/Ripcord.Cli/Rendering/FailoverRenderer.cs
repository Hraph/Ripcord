using System.Text;
using Ripcord.Application.Failover;
using Ripcord.Domain;
using Ripcord.Domain.Failover;

namespace Ripcord.Cli.Rendering;

/// The plan read before anyone confirms, and the account read afterwards.
///
/// Same constraints as the other renderers — fixed columns, 1024×768, no colour, nothing that
/// depends on terminal width. This one has one extra obligation: **every step says which host
/// it runs on**, in a column of its own rather than inside a sentence. Ripcord drives only the
/// host it is on, so the operator has to see at a glance which half is theirs to run here and
/// which half means walking to the other machine.
public static class FailoverRenderer
{
    private const int StepColumn = 5;
    private const int HostColumn = 16;
    private const int OutcomeColumn = 13;

    public static string Render(FailoverRunReport report, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(report);

        bool dryRun = report.Steps.Any(step => step.Outcome == StepOutcome.Planned);

        StringBuilder output = new();

        output.AppendLine(Layout.Banner(
            dryRun ? "RIPCORD FAILOVER (DRY RUN)" : "RIPCORD FAILOVER", now));
        output.AppendLine($"ripcord {BuildInfo.VersionWithCommit}");
        output.AppendLine();
        output.AppendLine($"  VM: {report.VmName}");
        output.AppendLine();

        AppendSteps(output, report);

        if (report.Halt is { } halt)
        {
            output.AppendLine();
            AppendBlock(output, "HALTED", halt);
        }

        AppendRollback(output, report);

        output.AppendLine();
        output.AppendLine($"  {report.Continuation}");

        AppendNextCommand(output, report);
        AppendFence(output, report);

        // Last, alone, and in capitals. Production is off at this point and the operator is
        // reading under pressure; anything after it would compete with the one line that has
        // to be acted on now.
        if (report.ManualRecovery is { } manual)
        {
            output.AppendLine();
            AppendBlock(output, "ACTION REQUIRED NOW", manual);
        }

        return Layout.Rendered(output);
    }

    private static void AppendSteps(StringBuilder output, FailoverRunReport report)
    {
        output.AppendLine(
            "  " + Layout.Pad("STEP", StepColumn) + Layout.Pad("HOST", HostColumn)
                + Layout.Pad("OUTCOME", OutcomeColumn) + "WHAT IT DOES");

        output.AppendLine("  " + Layout.Line(StepColumn + HostColumn + OutcomeColumn + 40));

        foreach (ExecutedStep step in report.Steps)
        {
            output.AppendLine(
                "  "
                + Layout.Pad(step.Step.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), StepColumn)
                + Layout.Pad(Layout.Truncate(step.Step.HostName, HostColumn - 1), HostColumn)
                + Layout.Pad(Word(step.Outcome), OutcomeColumn)
                + Layout.Truncate(step.Step.Description, 40));

            // The reason a step failed goes directly beneath it rather than in a footnote:
            // the operator is looking at the row that stopped, not scrolling for a list.
            if (step.FailureMessage is { } failure)
            {
                foreach (string line in Layout.Wrap(failure, 60))
                {
                    output.AppendLine("       " + line);
                }
            }
        }
    }

    /// The half of the sequence this host cannot run, as the line to type on the other one.
    ///
    /// Ripcord drives only the host it is on, so every run that succeeds ends with the operator
    /// walking to the other machine. Naming the host and the step number is not enough for
    /// somebody standing at a KVM under pressure: they need the command, spelled out, so it can
    /// be read off one screen and typed into another without composing it from memory.
    private static void AppendNextCommand(StringBuilder output, FailoverRunReport report)
    {
        ExecutedStep? elsewhere = report.Steps.FirstOrDefault(step =>
            step.Outcome is StepOutcome.NotThisHost or StepOutcome.Planned);

        if (elsewhere is null)
        {
            return;
        }

        bool dryRun = elsewhere.Outcome == StepOutcome.Planned;

        output.AppendLine();
        output.AppendLine(
            dryRun
                ? $"  START ON {elsewhere.Step.HostName}"
                : $"  NEXT, ON {elsewhere.Step.HostName}");

        output.AppendLine(
            $"    {Invocation(report.Operation)} --vm {report.VmName}"
                + (dryRun ? " --dry-run" : ""));

        // The other host runs a different half of the same plan, so the same command there
        // does something different — and re-running it here does nothing, because the sequence
        // re-derives where it is from what the hosts report rather than from a counter.
        output.AppendLine();
        output.AppendLine(
            "    The same command on either host runs only that host's steps, and running it");
        output.AppendLine(
            "    twice is safe: Ripcord works out where the pair is from the pair itself.");
    }

    /// The step easiest to leave out and costliest to forget. Production is now running here,
    /// and the other host still holds a copy of every VM that moved, set to start itself — so
    /// the operator is told what to do the moment it comes back, while the screen is in front
    /// of them rather than in a runbook they will look for later.
    private static void AppendFence(StringBuilder output, FailoverRunReport report)
    {
        if (report.FenceOnReturn is not { } host)
        {
            return;
        }

        output.AppendLine();
        output.AppendLine($"  WHEN {host} COMES BACK, BEFORE ANYTHING ELSE, ON THAT HOST");
        output.AppendLine("    ripcord fence");
        output.AppendLine();

        foreach (string line in Layout.Wrap(
            "It still holds a copy of every VM that moved, set to start itself. Both hosts "
                + "are on the same switch, so letting it boot puts two copies of the same "
                + "machine on the same subnet — and for a domain controller there is no "
                + "sequence here that repairs that.",
            68))
        {
            output.AppendLine("    " + line);
        }
    }

    /// The command the operator typed, so the line they are handed is one they can type back.
    ///
    /// `failback` is its own verb and refuses `--scenario`, so it is not a third word slotted
    /// into the same sentence: handing back `failover --scenario planned` after a failback
    /// would fail when typed, and — worse — that command builds the same plan with the two
    /// hosts the other way round.
    private static string Invocation(FailoverOperation operation) =>
        operation switch
        {
            FailoverOperation.Failback => "ripcord failback",
            FailoverOperation.UnplannedFailover => "ripcord failover --scenario unplanned",
            _ => "ripcord failover --scenario planned",
        };

    private static void AppendRollback(StringBuilder output, FailoverRunReport report)
    {
        if (report.Rollback is not { } rollback)
        {
            return;
        }

        output.AppendLine();

        AppendBlock(
            output,
            rollback.Succeeded ? "ROLLED BACK" : "ROLLBACK FAILED",
            rollback.Succeeded
                ? "this host was put back as it was; nothing was failed over"
                : $"this host could not be put back: {rollback.FailureMessage}");
    }

    /// Words, never colour, and never an abbreviation that reads as its opposite when skimmed.
    /// "planned" and "done" are the pair most likely to be confused, so they share no prefix.
    private static string Word(StepOutcome outcome) =>
        outcome switch
        {
            StepOutcome.Planned => "would run",
            StepOutcome.Done => "done",
            StepOutcome.Failed => "FAILED",
            StepOutcome.NotThisHost => "other host",
            StepOutcome.AlreadyDone => "already done",
            _ => "unknown",
        };

    private static void AppendBlock(StringBuilder output, string title, string body)
    {
        output.AppendLine($"  {title}");

        foreach (string line in Layout.Wrap(body, 68))
        {
            output.AppendLine("    " + line);
        }
    }
}
