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
    bool DryRun,
    bool Unattended = false);

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
    IReadOnlyList<string> UnconfirmedDiskSets,
    bool DryRun,
    bool Interrupted,
    bool Unattended = false)
{
    /// Ordered by what the reader has to do about it, not by severity of intent.
    ///
    /// 5 first: a cleanup that failed left a test VM behind, holding disk on the target with
    /// a relationship in a state nobody chose. That is the intermediate state of decision D7,
    /// and it outranks everything — including an interruption, because "nothing changed" is
    /// then simply false. Reporting it as 1 would send the reader to `ripcord check`, where
    /// they would find no critical violation and conclude it was transient.
    ///
    /// 4 next: refused or interrupted with nothing left behind.
    ///
    /// 1 for a test VM that would not come up. D7's row says "a critical rule is violated",
    /// but its own preamble defines the class as "the infrastructure is wrong" as against
    /// "the tool itself failed" — and a replica that will not boot is the most important
    /// thing that code could ever carry.
    public ExitCode Code
    {
        get
        {
            if (this.Results.Any(result => result.Cleanup is { Succeeded: false }))
            {
                return ExitCode.IntermediateState;
            }

            if (this.Refusal is { Refuses: true } || this.Interrupted)
            {
                return ExitCode.Refused;
            }

            return this.Results.Any(DidNotBoot) ? ExitCode.CriticalFinding : ExitCode.Success;
        }
    }

    private static bool DidNotBoot(VmTestFailoverResult result) =>
        result.Status is not (TestFailoverStatus.Booted or TestFailoverStatus.Planned);
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

        IReadOnlyList<Orphan> orphans;

        try
        {
            orphans = await this
                .ScanForOrphansAsync(plan, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Interrupted before the precondition was even evaluated. Nothing was touched,
            // and saying so is the whole purpose of exit 4 — letting this escape would reach
            // the CLI's generic handler and report a local access failure instead.
            return new TestFailoverReport(
                startedAt, null, [], [], [], plan.DryRun, true, plan.Unattended);
        }

        PreconditionRefusal refusal =
            TestFailoverPrecondition.Evaluate(plan.Check, plan.Unattended);

        if (refusal.Refuses)
        {
            return new TestFailoverReport(
                startedAt,
                refusal,
                orphans,
                [],
                Unconfirmed(plan),
                plan.DryRun,
                false,
                plan.Unattended);
        }

        if (plan.DryRun)
        {
            return new TestFailoverReport(
                startedAt,
                null,
                orphans,
                [.. plan.VmNames.Select(Planned)],
                Unconfirmed(plan),
                true,
                false,
                plan.Unattended);
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
            startedAt,
            null,
            orphans,
            results,
            Unconfirmed(plan),
            false,
            interrupted,
            plan.Unattended);
    }

    /// Named in the report rather than blocking: a guest missing a data disk still boots and
    /// still answers the heartbeat, so "booted" would otherwise read as "complete".
    private static IReadOnlyList<string> Unconfirmed(TestFailoverPlan plan) =>
        TestFailoverPrecondition.UnconfirmedDiskSets(plan.Check);

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
            // Cancellation is deliberately not caught here: it is not a failed scan, it is
            // the operator stopping the run, and the caller turns it into exit 4.
            return [];
        }
    }

    private async Task<VmTestFailoverResult> RunOneAsync(
        string vmName, TestFailoverPlan plan, CancellationToken cancellationToken)
    {
        TestVm? testVm = null;
        IReadOnlyList<string>? before = null;
        bool creating = false;

        try
        {
            await provider
                .AttachTestNetworkAsync(vmName, plan.TestSwitch, cancellationToken)
                .ConfigureAwait(false);

            before = await this.TestVmNamesAsync(cancellationToken).ConfigureAwait(false);
            creating = true;

            testVm = await provider
                .StartTestFailoverAsync(vmName, cancellationToken)
                .ConfigureAwait(false);

            IsolationAssessment isolation = Isolation.Of(
                testVm.Adapters,
                plan.TestSwitch,
                await provider.GetSwitchesAsync(cancellationToken).ConfigureAwait(false));

            if (!isolation.IsIsolated)
            {
                return await this
                    .CleanUpAsync([testVm.Name], NotIsolated(vmName, isolation))
                    .ConfigureAwait(false);
            }

            if (Isolation.OffTheTestSwitch(testVm.Adapters, plan.TestSwitch) is { Count: > 0 } off)
            {
                return await this
                    .CleanUpAsync([testVm.Name], NotOnTheTestSwitch(vmName, off, plan.TestSwitch!))
                    .ConfigureAwait(false);
            }

            return await this
                .CleanUpAsync([testVm.Name], await this
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

            if (testVm is not null)
            {
                return await this.CleanUpAsync([testVm.Name], failure).ConfigureAwait(false);
            }

            if (!creating)
            {
                return failure;
            }

            // A create can fail after having created. Whatever test VM appeared since the
            // scan before it is this run's; the replica itself is never a candidate.
            IReadOnlyList<string>? after =
                await this.TestVmNamesAsync(CancellationToken.None).ConfigureAwait(false);

            if (before is null || after is null)
            {
                return failure with
                {
                    FailureMessage = (failure.FailureMessage ?? "interrupted")
                        + ". A test VM may be left behind: the next run lists it",
                };
            }

            string[] appeared =
                [.. after.Where(name => !before.Contains(name, StringComparer.OrdinalIgnoreCase))];

            return appeared.Length > 0
                ? await this.CleanUpAsync(appeared, failure).ConfigureAwait(false)
                : failure;
        }
    }

    /// Null when the host could not say: the caller then cannot tell a new test VM from an
    /// old one, and must not guess.
    private async Task<IReadOnlyList<string>?> TestVmNamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<TestVm> testVms = await provider
                .GetTestVmsAsync(cancellationToken)
                .ConfigureAwait(false);

            return [.. testVms.Select(vm => vm.Name)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
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

                // The guest cannot answer at all, so waiting longer answers nothing.
                case Heartbeat.CannotConfirm:
                case Heartbeat.Unreadable:
                    return new VmTestFailoverResult(
                        vmName,
                        TestFailoverStatus.BootedWithoutHeartbeat,
                        null,
                        [],
                        heartbeat == Heartbeat.CannotConfirm
                            ? "the guest cannot answer the host - an incompatible integration "
                                + "services version, or a paused VM"
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
                    $"no heartbeat within {(int)timing.HeartbeatTimeout.TotalSeconds}s. The "
                        + "guest may still be booting, or may have no integration services - "
                        + "Hyper-V reports both the same way and does not distinguish them",
                    null);
            }

            await Task.Delay(timing.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// Step 6, and the reason `Compensation` exists. Discard policy: one bounded attempt,
    /// then a loud line in the report. The test VM is a copy — a second attempt at destroying
    /// one that refused to die is unlikely to fare better, and the honest move is to say so.
    ///
    /// Always by the test VM's own name: the replicated VM's would name production.
    private async Task<VmTestFailoverResult> CleanUpAsync(
        IReadOnlyList<string> testVmNames, VmTestFailoverResult result) =>
        result with
        {
            Cleanup = await Compensation
                .RunAsync(
                    async token =>
                    {
                        foreach (string testVmName in testVmNames)
                        {
                            await provider
                                .StopTestFailoverAsync(testVmName, token)
                                .ConfigureAwait(false);
                        }
                    },
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

    private static VmTestFailoverResult NotOnTheTestSwitch(
        string vmName, IReadOnlyList<string> adapters, string testSwitch) =>
        new(
            vmName,
            TestFailoverStatus.Failed,
            null,
            [],
            $"{string.Join(", ", adapters)} not on '{testSwitch}': the test VM would boot "
                + "without the network test_failover_switch names",
            null);

    private static VmTestFailoverResult Planned(string vmName) =>
        new(vmName, TestFailoverStatus.Planned, null, [], null, null);

    private static VmTestFailoverResult Skipped(string vmName) =>
        new(vmName, TestFailoverStatus.Skipped, null, [], "not attempted", null);
}
