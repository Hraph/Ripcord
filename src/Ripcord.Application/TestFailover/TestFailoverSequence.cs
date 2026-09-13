using Ripcord.Domain.Checks;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.TestFailover;
using Ripcord.Domain;
using Ripcord.Ports.Replication;
using Ripcord.Ports;

namespace Ripcord.Application.TestFailover;

/// How long the sequence waits, and how long it gives itself to clean up. Passed in rather
/// than hard-coded so the failure paths are exercisable without any test waiting in real time.
public sealed record TestFailoverTiming(
    TimeSpan HeartbeatTimeout, TimeSpan PollInterval, TimeSpan CleanupDeadline)
{
    /// A Windows guest that has not reported a heartbeat in ten minutes is not slow, it is
    /// broken — and the report is more useful than a longer wait.
    public static readonly TestFailoverTiming Default = new(
        HeartbeatTimeout: TimeSpan.FromMinutes(10),
        PollInterval: TimeSpan.FromSeconds(5),
        CleanupDeadline: TimeSpan.FromMinutes(2));
}

/// What to run, against a verdict already reached. The check is an input rather than
/// something this class fetches: the sequence is about driving the host, and handing it the
/// report keeps every failure path reachable from a test.
public sealed record TestFailoverPlan(
    CheckReport Check,
    IReadOnlyList<string> VmNames,
    string? TestSwitch,
    TimeSpan OrphanAfter,
    bool DryRun);

public enum TestFailoverStatus
{
    Booted,

    /// Started, and the guest cannot be asked whether it came up. Never a pass.
    BootedWithoutHeartbeat,

    NoHeartbeat,
    RefusedNotIsolated,
    Failed,

    /// Not attempted, because the run was interrupted before reaching it.
    Skipped,

    /// `--dry-run`: what would have been attempted.
    Planned,
}

public sealed record VmTestFailoverResult(
    string Name,
    TestFailoverStatus Status,
    TimeSpan? TimeToHeartbeat,
    IReadOnlyList<IsolationBreach> Breaches,
    string? FailureMessage,
    Compensation? Cleanup);

/// Everything a run produced, and the exit code it implies.
public sealed record TestFailoverReport(
    DateTimeOffset StartedAt,
    PreconditionRefusal? Refusal,
    IReadOnlyList<Orphan> Orphans,
    IReadOnlyList<VmTestFailoverResult> Results,
    bool DryRun,
    bool Interrupted)
{
    /// 4 is "nothing changed": refused by the precondition, or interrupted by the operator.
    /// 1 is the infrastructure not being in the state it must be in — a VM that would not
    /// come up, or a test VM this run failed to destroy. 0 is a monthly test that passed.
    public ExitCode Code
    {
        get
        {
            if (this.Refusal is { Refuses: true } || this.Interrupted)
            {
                return ExitCode.Refused;
            }

            return this.Results.Any(Unsuccessful) ? ExitCode.CriticalFinding : ExitCode.Success;
        }
    }

    private static bool Unsuccessful(VmTestFailoverResult result) =>
        result.Status is not (TestFailoverStatus.Booted or TestFailoverStatus.Planned)
        || result.Cleanup is { Succeeded: false };
}

/// Steps 2 to 6 of milestone 3, one VM at a time.
///
/// Two properties are load-bearing and every failure path here exists to pin one of them.
///
/// The first is that the test VM is inspected *after* it exists rather than predicted before.
/// `TestReplicaSwitchName` is read/write, so what a test VM will be attached to cannot be
/// computed from the replica — and a forecast presented as a verification is the same defect
/// as a name compared in place of a property.
///
/// The second is that the stop always runs. It goes through `Compensation`, which holds no
/// caller token, because a `finally` awaiting on a cancelled token is a cleanup that does not
/// run — and Ctrl+C is the case that matters, being the one an operator produces on purpose
/// when something looks wrong.
public sealed class TestFailoverSequence(
    IHypervProvider provider, IClock clock, TestFailoverTiming timing)
{
    public async Task<TestFailoverReport> RunAsync(
        TestFailoverPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        DateTimeOffset startedAt = clock.UtcNow;

        IReadOnlyList<Orphan> orphans = await this
            .ScanForOrphansAsync(plan, cancellationToken)
            .ConfigureAwait(false);

        PreconditionRefusal refusal = TestFailoverPrecondition.Evaluate(plan.Check);

        if (refusal.Refuses)
        {
            return new TestFailoverReport(startedAt, refusal, orphans, [], plan.DryRun, false);
        }

        if (plan.DryRun)
        {
            return new TestFailoverReport(
                startedAt,
                null,
                orphans,
                [.. plan.VmNames.Select(Planned)],
                true,
                false);
        }

        List<VmTestFailoverResult> results = [];
        bool interrupted = false;

        foreach (string vmName in plan.VmNames)
        {
            if (interrupted)
            {
                results.Add(Skipped(vmName));
                continue;
            }

            VmTestFailoverResult result = await this
                .RunOneAsync(vmName, plan, cancellationToken)
                .ConfigureAwait(false);

            results.Add(result);

            // One at a time, and no further: the operator asked for it to stop.
            interrupted = cancellationToken.IsCancellationRequested;
        }

        return new TestFailoverReport(
            startedAt, null, orphans, results, false, interrupted);
    }

    /// Reported even when the run is refused: a test VM left behind by an earlier run is
    /// exactly what the operator needs to know about when this one will not start.
    private async Task<IReadOnlyList<Orphan>> ScanForOrphansAsync(
        TestFailoverPlan plan, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<TestVm> testVms = await provider
                .GetTestVmsAsync(cancellationToken)
                .ConfigureAwait(false);

            return Orphans.Detect(testVms, plan.OrphanAfter, clock.UtcNow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A scan that failed must not stop a test failover that would otherwise run.
            return [];
        }
    }

    private async Task<VmTestFailoverResult> RunOneAsync(
        string vmName, TestFailoverPlan plan, CancellationToken cancellationToken)
    {
        bool created = false;

        try
        {
            await provider
                .AttachTestNetworkAsync(vmName, plan.TestSwitch, cancellationToken)
                .ConfigureAwait(false);

            TestVm testVm = await provider
                .StartTestFailoverAsync(vmName, cancellationToken)
                .ConfigureAwait(false);

            created = true;

            IsolationAssessment isolation = Isolation.Of(
                testVm.Adapters,
                plan.TestSwitch,
                await provider.GetSwitchesAsync(cancellationToken).ConfigureAwait(false));

            if (!isolation.IsIsolated)
            {
                return await this
                    .CleanUpAsync(vmName, NotIsolated(vmName, isolation))
                    .ConfigureAwait(false);
            }

            return await this
                .CleanUpAsync(vmName, await this
                    .BootAsync(vmName, testVm, cancellationToken)
                    .ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            VmTestFailoverResult failure = new(
                vmName,
                exception is OperationCanceledException
                    ? TestFailoverStatus.Skipped
                    : TestFailoverStatus.Failed,
                null,
                [],
                exception is OperationCanceledException ? null : exception.Message,
                null);

            // Attempted whenever the creation was attempted, because a create can fail after
            // having created. A stop that reports "nothing to stop" is noise; a test VM left
            // behind because nobody tried is a broken next run.
            return created || exception is not OperationCanceledException
                ? await this.CleanUpAsync(vmName, failure).ConfigureAwait(false)
                : failure;
        }
    }

    private async Task<VmTestFailoverResult> BootAsync(
        string vmName, TestVm testVm, CancellationToken cancellationToken)
    {
        await provider.StartTestVmAsync(testVm.Name, cancellationToken).ConfigureAwait(false);

        DateTimeOffset deadline = clock.UtcNow.Add(timing.HeartbeatTimeout);
        DateTimeOffset startedAt = clock.UtcNow;

        while (true)
        {
            Heartbeat heartbeat = await provider
                .ReadHeartbeatAsync(testVm.Name, cancellationToken)
                .ConfigureAwait(false);

            switch (heartbeat)
            {
                case Heartbeat.Ok:
                    return new VmTestFailoverResult(
                        vmName,
                        TestFailoverStatus.Booted,
                        clock.UtcNow - startedAt,
                        [],
                        null,
                        null);

                // The guest cannot be asked at all, so waiting longer answers nothing.
                case Heartbeat.NotInstalled:
                case Heartbeat.Unreadable:
                    return new VmTestFailoverResult(
                        vmName,
                        TestFailoverStatus.BootedWithoutHeartbeat,
                        null,
                        [],
                        heartbeat == Heartbeat.NotInstalled
                            ? "the guest has no integration services, so its boot cannot be "
                                + "confirmed from the host"
                            : "the heartbeat could not be read",
                        null);

                default:
                    break;
            }

            if (clock.UtcNow >= deadline)
            {
                return new VmTestFailoverResult(
                    vmName,
                    TestFailoverStatus.NoHeartbeat,
                    null,
                    [],
                    $"no heartbeat within {(int)timing.HeartbeatTimeout.TotalSeconds}s",
                    null);
            }

            await Task.Delay(timing.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// Step 6, and the reason `Compensation` exists. Discard policy: one bounded attempt,
    /// then a loud line in the report. The test VM is a copy — a second attempt at destroying
    /// one that refused to die is unlikely to fare better, and the honest move is to say so.
    private async Task<VmTestFailoverResult> CleanUpAsync(
        string vmName, VmTestFailoverResult result) =>
        result with
        {
            Cleanup = await Compensation
                .RunAsync(
                    token => provider.StopTestFailoverAsync(vmName, token),
                    timing.CleanupDeadline)
                .ConfigureAwait(false),
        };

    private static VmTestFailoverResult NotIsolated(
        string vmName, IsolationAssessment isolation) =>
        new(
            vmName,
            TestFailoverStatus.RefusedNotIsolated,
            null,
            isolation.Breaches,
            "the test VM is not isolated from the production network",
            null);

    private static VmTestFailoverResult Planned(string vmName) =>
        new(vmName, TestFailoverStatus.Planned, null, [], null, null);

    private static VmTestFailoverResult Skipped(string vmName) =>
        new(vmName, TestFailoverStatus.Skipped, null, [], "not attempted", null);
}
