using Ripcord.Application.TestFailover;
using Ripcord.Domain;
using Ripcord.Domain.Failover;
using Ripcord.Domain.Replication;
using Ripcord.Ports.Replication;

namespace Ripcord.Application.Failover;

public sealed record FailoverRequest(
    string VmName, string MachineName, string ExpectedSwitchName, bool DryRun);

public enum StepOutcome
{
    /// `--dry-run`: what would be done, and nothing was.
    Planned,

    Done,
    Failed,

    /// Belongs to the other host. Shown rather than hidden, because the operator has to go and
    /// run it there.
    NotThisHost,

    /// Already done before this invocation started, according to the hosts themselves.
    AlreadyDone,
}

public sealed record ExecutedStep(
    FailoverStep Step, StepOutcome Outcome, string? FailureMessage = null);

/// What one invocation did, and what the operator has to do next.
public sealed record FailoverRunReport(
    string VmName,
    IReadOnlyList<ExecutedStep> Steps,
    ExitCode Code,
    string Continuation,
    Compensation? Rollback = null,
    string? ManualRecovery = null,
    string? Halt = null);

/// How long the restore is given, and how many times it is attempted.
///
/// Both are deliberately caller-side rather than baked into `Compensation`: milestone 3's
/// discard policy is one bounded attempt because a second try at destroying a copy is unlikely
/// to fare better. Putting production back is the opposite — giving up quietly is the failure,
/// so it retries.
public sealed record FailoverTiming(TimeSpan RestoreDeadline, int RestoreAttempts)
{
    public static readonly FailoverTiming Default = new(TimeSpan.FromMinutes(2), 3);

    public static readonly FailoverTiming ForTests = new(TimeSpan.FromSeconds(5), 3);
}

/// Carries out this host's half of a planned failover.
///
/// Ripcord drives only the host it runs on (D29), so an invocation performs the steps the plan
/// assigns to this machine and then stops, naming the host that continues. The alternative — a
/// channel that mutates the peer — was rejected: in an unplanned failover the peer is dead by
/// definition, so a cross-host execution path is unavailable in precisely the case the tool
/// exists for, and a mechanism that cannot work when it is needed is not a mechanism.
///
/// Where it has got to is re-derived from `FailoverProgress`, never from a stored cursor, so
/// running this twice is safe and running it on the wrong host is refused rather than obeyed.
public sealed class PlannedFailoverSequence(IHypervProvider provider, FailoverTiming timing)
{
    public async Task<FailoverRunReport> RunAsync(
        FailoverPlan plan,
        FailoverProgress progress,
        SplitBrain splitBrain,
        FailoverRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(splitBrain);
        ArgumentNullException.ThrowIfNull(request);

        // Nothing is attempted while both hosts may be claiming the VM. There is no sequence
        // that recovers from that, so one more step can only deepen it.
        if (splitBrain.HaltsMutation)
        {
            return Halted(plan, progress, ExitCode.Refused, splitBrain.Evidence!);
        }

        if (!progress.CanResume)
        {
            return Halted(plan, progress, ExitCode.Refused, progress.Explanation);
        }

        if (progress.NextStep is not { } next)
        {
            return new FailoverRunReport(
                request.VmName, Mirror(plan, progress), ExitCode.Success,
                "every step has already run");
        }

        if (!next.RunsOn(request.MachineName))
        {
            return new FailoverRunReport(
                request.VmName, Mirror(plan, progress), ExitCode.Refused,
                $"step {next.Number} runs on {next.HostName}; nothing was changed here");
        }

        return request.DryRun
            ? new FailoverRunReport(
                request.VmName,
                [.. plan.Steps.Select(step => new ExecutedStep(step, StepOutcome.Planned))],
                ExitCode.Success,
                $"nothing was changed. Re-run without --dry-run on {next.HostName} to apply")
            : await this.ExecuteAsync(plan, progress, request, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<FailoverRunReport> ExecuteAsync(
        FailoverPlan plan,
        FailoverProgress progress,
        FailoverRequest request,
        CancellationToken cancellationToken)
    {
        List<ExecutedStep> results = [];
        List<FailoverStep> performed = [];

        foreach (StepProgress step in progress.Steps)
        {
            if (step.State == StepState.Done)
            {
                results.Add(new ExecutedStep(step.Step, StepOutcome.AlreadyDone));
                continue;
            }

            if (!step.Step.RunsOn(request.MachineName))
            {
                results.Add(new ExecutedStep(step.Step, StepOutcome.NotThisHost));
                continue;
            }

            try
            {
                await this.PerformAsync(step.Step, request, cancellationToken)
                    .ConfigureAwait(false);

                results.Add(new ExecutedStep(step.Step, StepOutcome.Done));
                performed.Add(step.Step);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                results.Add(
                    new ExecutedStep(step.Step, StepOutcome.Failed, exception.Message));

                return await this.UnwindAsync(
                        request, results, performed, step.Step, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new FailoverRunReport(
            request.VmName, results, ExitCode.Success, NextHost(results));
    }

    private Task PerformAsync(
        FailoverStep step, FailoverRequest request, CancellationToken cancellationToken) =>
        step.Action switch
        {
            FailoverAction.ShutDownVm =>
                provider.ShutDownVmAsync(request.VmName, cancellationToken),
            FailoverAction.PrepareFailover =>
                provider.PrepareFailoverAsync(request.VmName, cancellationToken),
            FailoverAction.StartFailover =>
                provider.StartFailoverAsync(request.VmName, cancellationToken),
            FailoverAction.ReverseReplication =>
                provider.ReverseReplicationAsync(request.VmName, cancellationToken),
            FailoverAction.StartVm =>
                provider.StartVmAsync(request.VmName, cancellationToken),
            FailoverAction.VerifyNetwork => this.VerifyNetworkAsync(request, cancellationToken),
            _ => Task.CompletedTask,
        };

    /// Step 6. Not a formality: a VM that boots without network is a failed failover, and the
    /// host-level checks all passed while that was true.
    private async Task VerifyNetworkAsync(
        FailoverRequest request, CancellationToken cancellationToken)
    {
        HostState local = await provider.GetLocalStateAsync(cancellationToken)
            .ConfigureAwait(false);

        VmReplicationState? vm = local.Vms.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, request.VmName, StringComparison.OrdinalIgnoreCase));

        if (vm?.Facts is not { } facts)
        {
            throw new InvalidOperationException(
                $"'{request.VmName}' is running, but its network could not be read, so the "
                    + "failover cannot be confirmed — treat it as unverified, not as working");
        }

        // Connected to the right switch is two conditions, not one. An adapter bound to the
        // expected switch but disconnected passes a name comparison and still leaves the guest
        // with no network.
        bool connected = facts.Adapters.Any(adapter =>
            adapter.IsConnected == true
            && string.Equals(
                adapter.SwitchName, request.ExpectedSwitchName, StringComparison.OrdinalIgnoreCase));

        if (!connected)
        {
            throw new InvalidOperationException(
                $"'{request.VmName}' started but no adapter is connected to "
                    + $"'{request.ExpectedSwitchName}', so it has no network on this host");
        }
    }

    /// Undo what **this invocation** performed, and only while nothing has begun writing.
    ///
    /// Once the VM has been started there is no unwinding: step 6 is a verification, and
    /// tearing down a live workload because a check failed would cause the outage the check
    /// exists to warn about. It is reported instead.
    private async Task<FailoverRunReport> UnwindAsync(
        FailoverRequest request,
        List<ExecutedStep> results,
        List<FailoverStep> performed,
        FailoverStep failed,
        CancellationToken cancellationToken)
    {
        if (failed.Action == FailoverAction.VerifyNetwork)
        {
            return new FailoverRunReport(
                request.VmName, results, ExitCode.CriticalFinding,
                $"'{request.VmName}' is running on this host but its network is wrong; fix the "
                    + "adapter here rather than failing back");
        }

        if (Undo(performed) is not { } undo)
        {
            // Nothing this invocation did needs undoing, but a mutating call was attempted and
            // failed — so "nothing changed" cannot be asserted, and 4 would assert it.
            return new FailoverRunReport(
                request.VmName, results, ExitCode.IntermediateState,
                "the step failed before anything else had been done here");
        }

        Compensation rollback = await this.RestoreAsync(undo, request, cancellationToken)
            .ConfigureAwait(false);

        return new FailoverRunReport(
            request.VmName,
            results,
            ExitCode.IntermediateState,
            rollback.Succeeded
                ? "this host was put back as it was; the pair was not failed over"
                : "THIS HOST IS NOT BACK AS IT WAS",
            rollback,
            rollback.Succeeded ? null : ManualStep(undo, request.VmName));
    }

    /// The last thing done is the only thing undone: each step's undo restores the state the
    /// step before it left, so unwinding one is enough to leave the pair coherent.
    private static FailoverAction? Undo(List<FailoverStep> performed) =>
        performed.Count == 0
            ? null
            : performed[^1].Action switch
            {
                // The VM was shut down and nothing else happened. Put it back on.
                FailoverAction.ShutDownVm => FailoverAction.StartVm,

                // Deliberately no arm for StartFailover. Undoing a failover would only arise if
                // steps 3 and 4 ran in one invocation, which cannot happen while the replica
                // cannot observe the primary's prepare (V35) — so an arm for it would be a
                // branch no test can reach. It belongs here when the cross-host handoff is
                // closed, and not before.
                _ => null,
            };

    /// The restore policy. `Compensation.RunAsync` is one bounded attempt by design — the right
    /// answer for discarding a copy — so the retry lives here, where it is visibly milestone
    /// 4's and not milestone 3's.
    private async Task<Compensation> RestoreAsync(
        FailoverAction undo, FailoverRequest request, CancellationToken cancellationToken)
    {
        Compensation last = Compensation.Failed("not attempted");

        for (int attempt = 0; attempt < timing.RestoreAttempts; attempt++)
        {
            last = await Compensation.RunAsync(
                    token => undo == FailoverAction.StartVm
                        ? provider.StartVmAsync(request.VmName, token)
                        : provider.CancelFailoverAsync(request.VmName, token),
                    timing.RestoreDeadline)
                .ConfigureAwait(false);

            if (last.Succeeded)
            {
                return last;
            }
        }

        return last;
    }

    /// Printed when the restore has run out of attempts. Production is down at this point, so
    /// the operator needs the command itself rather than a description of the problem.
    private static string ManualStep(FailoverAction undo, string vmName) =>
        undo == FailoverAction.StartVm
            ? $"Ripcord could not restart '{vmName}' after a failed step, so it is still shut "
                + $"down. Start it on this host now: Start-VM -Name '{vmName}'"
            : $"Ripcord could not cancel the failover of '{vmName}', so this host may still be "
                + $"holding it. Check it and cancel by hand: Stop-VMFailover -VMName '{vmName}'";

    private static FailoverRunReport Halted(
        FailoverPlan plan, FailoverProgress progress, ExitCode code, string why) =>
        new(plan.VmName, Mirror(plan, progress), code, "nothing was changed", null, null, why);

    /// The plan as the hosts currently report it, with nothing attempted.
    private static List<ExecutedStep> Mirror(FailoverPlan plan, FailoverProgress progress) =>
        [
            .. progress.Steps.Select(step => new ExecutedStep(
                step.Step,
                step.State == StepState.Done ? StepOutcome.AlreadyDone : StepOutcome.NotThisHost)),
        ];

    private static string NextHost(List<ExecutedStep> results) =>
        results.FirstOrDefault(step => step.Outcome == StepOutcome.NotThisHost) is { } remaining
            ? $"continue on {remaining.Step.HostName}, from step {remaining.Step.Number}"
            : "the sequence is complete on both hosts";
}
