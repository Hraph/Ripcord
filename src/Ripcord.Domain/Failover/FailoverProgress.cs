using Ripcord.Domain.Replication;

namespace Ripcord.Domain.Failover;

public enum StepState
{
    /// Observed to have happened, or implied by something further along that could only be
    /// true if it had.
    Done,

    NotDone,

    /// The evidence for this step could not be read. Its own answer, because the two
    /// alternatives fail in opposite directions: treating it as done steps over a change that
    /// never happened, and treating it as not done repeats one that did.
    Indeterminate,
}

public sealed record StepProgress(FailoverStep Step, StepState State);

/// Where a sequence has got to, re-derived from what the two hosts report.
///
/// Deliberately **not** a stored cursor (D29). A position written down survives a crash by
/// lying about it, and the moment it disagrees with the hosts it is worse than nothing. It
/// also makes the sequence safe to re-run: an operator who runs the command twice, or on the
/// wrong host, is told where things actually stand rather than advancing a counter.
///
/// Evidence is read from the furthest point forward, not step by step. Several steps leave no
/// trace of their own, but a later one that could only be true if they had run settles them —
/// so `Start-VMFailover -Prepare` needs no observable signature of its own as long as the
/// failover it precedes has one.
public sealed record FailoverProgress(
    IReadOnlyList<StepProgress> Steps, FailoverStep? NextStep, bool CanResume, string Explanation)
{
    public static FailoverProgress Of(
        FailoverPlan plan, VmReplicationState? onSource, VmReplicationState? onTarget)
    {
        ArgumentNullException.ThrowIfNull(plan);

        int reached = FurthestReached(plan, onTarget);
        List<StepProgress> steps = [];

        foreach (FailoverStep step in plan.Steps)
        {
            steps.Add(new StepProgress(step, StateOf(step, reached, onSource)));
        }

        StepProgress? next = steps.FirstOrDefault(step => step.State != StepState.Done);

        // Indeterminate anywhere up to and including the next step means the sequence does not
        // know where it is. Beyond it the question has not arisen yet.
        bool blocked = steps
            .TakeWhile(step => step != next)
            .Append(next!)
            .Any(step => step is { State: StepState.Indeterminate });

        return new FailoverProgress(
            steps,
            next?.Step,
            next is not null && !blocked,
            Explain(next, blocked, reached));
    }

    /// The highest step number the target's own state proves has happened. Each piece of
    /// evidence is a condition the target could not be in unless every earlier step had run.
    ///
    /// Evidence names an **action**, and the plan is then asked where that action sits. The
    /// unplanned plan reaches a failed-over target in one step where the planned one takes
    /// three, so a position derived from a step number would place it two steps too far along;
    /// and evidence for an action a plan does not contain — a reversal, in an unplanned run —
    /// falls through to the next-strongest reading rather than promoting the position to a
    /// step that is not there.
    private static int FurthestReached(FailoverPlan plan, VmReplicationState? onTarget)
    {
        if (onTarget is null)
        {
            return 0;
        }

        foreach (FailoverAction proven in Evidence(onTarget))
        {
            if (plan.Steps.FirstOrDefault(step => step.Action == proven) is { } step)
            {
                return step.Number;
            }
        }

        return 0;
    }

    /// Strongest reading first: a running VM proves the start and everything before it.
    private static IEnumerable<FailoverAction> Evidence(VmReplicationState onTarget)
    {
        if (onTarget.PowerState == VmPowerState.Running)
        {
            yield return FailoverAction.StartVm;
        }

        if (onTarget.Role == ReplicationRole.Primary)
        {
            yield return FailoverAction.ReverseReplication;
        }

        if (onTarget.State is ReplicationState.Recovered or ReplicationState.Committed)
        {
            yield return FailoverAction.StartFailover;
        }
    }

    private static StepState StateOf(
        FailoverStep step, int reached, VmReplicationState? onSource)
    {
        if (step.Number <= reached)
        {
            return StepState.Done;
        }

        return step.Action switch
        {
            // The shutdown's evidence is the VM being off on the host it was running on.
            // Unknown and absent both mean nobody could see, which is not a reading of "still
            // running" — shutting down a second time is harmless, but concluding the shutdown
            // happened when it did not would carry the sequence past a live VM.
            FailoverAction.ShutDownVm => onSource?.PowerState switch
            {
                VmPowerState.Off => StepState.Done,
                null or VmPowerState.Unknown => StepState.Indeterminate,
                _ => StepState.NotDone,
            },

            // The prepare leaves no state of its own this binary can name — which
            // `ReplicationState` a primary reports after `-Prepare` is unverified (V35). It is
            // reported NotDone rather than Indeterminate on purpose: `reached` above settles it
            // whenever the failover that follows has run, so the only case landing here is one
            // where the failover demonstrably has not. Re-issuing a prepare in that situation
            // is an error the sequence handles, and its undo is the benign row of
            // `Stop-VMFailover`. Blocking the whole sequence on an unobservable step would
            // make the common path unusable to protect against the recoverable case.
            FailoverAction.PrepareFailover => StepState.NotDone,

            // A verification, not a change. It is never inferred from state: that is the whole
            // point of it, and a step 6 that reported itself already done would confirm a
            // network nobody looked at.
            FailoverAction.VerifyNetwork => StepState.NotDone,

            _ => StepState.NotDone,
        };
    }

    private static string Explain(StepProgress? next, bool blocked, int reached)
    {
        if (next is null)
        {
            return "every step of this sequence has already run";
        }

        if (blocked)
        {
            return $"step {next.Step.Number} cannot be placed from what the hosts report, so "
                + "the sequence will not resume on its own";
        }

        return reached == 0
            ? $"nothing has run yet; the sequence starts at step {next.Step.Number} on "
                + next.Step.HostName
            : $"steps 1 to {reached} have already run; the sequence continues at step "
                + $"{next.Step.Number} on {next.Step.HostName}";
    }
}
