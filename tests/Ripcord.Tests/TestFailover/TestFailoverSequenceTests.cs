using Ripcord.Adapters.Fake;
using Ripcord.Application.TestFailover;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;
using Ripcord.Domain.TestFailover;
using Ripcord.Domain;
using Ripcord.Ports;
using Ripcord.Tests.Checks;

namespace Ripcord.Tests.TestFailover;

/// The sequence, and above all its failure paths. Step 6 — the automatic `Stop-VMFailover` —
/// is not optional and must survive an error in any earlier step and an interruption by the
/// operator. An orphaned test VM holds disk on the target and quietly breaks the next test,
/// so every test here that breaks something also asserts that the stop still ran.
public class TestFailoverSequenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_healthy_vm_is_attached_created_inspected_started_and_stopped_in_order()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));

        TestFailoverReport report = await Run(host, "VM-DC-01");

        VmTestFailoverResult result = Assert.Single(report.Results);

        Assert.Equal(TestFailoverStatus.Booted, result.Status);
        Assert.Equal(
            [
                "attach:VM-DC-01:vSwitch-ISOLATED",
                "create:VM-DC-01",
                "switches",
                "start:VM-DC-01 (test copy)",
                "heartbeat:VM-DC-01 (test copy)",
                "stop:VM-DC-01",
            ],
            host.Calls.Where(call => call != "test-vms"));
    }

    /// Refused before anything is touched. The whole point of exit 4 is that nothing changed.
    [Fact]
    public async Task A_refused_precondition_touches_nothing_and_exits_four()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));

        TestFailoverReport report = await Run(
            host,
            "VM-DC-01",
            check: Refusing());

        Assert.Equal(ExitCode.Refused, report.Code);
        Assert.NotNull(report.Refusal);
        Assert.Empty(report.Results);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("create", StringComparison.Ordinal));
    }

    /// The test VM exists at this point, so it is destroyed rather than started. This is the
    /// case the whole milestone is shaped around: a domain controller that would otherwise
    /// boot onto the production LAN with the identity of the live one.
    [Fact]
    public async Task A_test_vm_that_is_not_isolated_is_destroyed_and_never_started()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start))
        {
            TestVmFactory = name => new TestVm(
                name + " (test copy)",
                null,
                [Adapter(FakeScenarios.ProductionSwitch, connected: true)]),
        };

        TestFailoverReport report = await Run(host, "VM-DC-01");

        VmTestFailoverResult result = Assert.Single(report.Results);

        Assert.Equal(TestFailoverStatus.RefusedNotIsolated, result.Status);
        Assert.NotEmpty(result.Breaches);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("start:", StringComparison.Ordinal));
        Assert.Contains("stop:VM-DC-01", host.Calls);
    }

    [Fact]
    public async Task A_vm_that_fails_to_start_is_still_cleaned_up()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start))
        {
            StartVmFailure = new InvalidOperationException("not enough memory on the host"),
        };

        TestFailoverReport report = await Run(host, "VM-DC-01");

        Assert.Equal(TestFailoverStatus.Failed, Assert.Single(report.Results).Status);
        Assert.Contains("not enough memory", Assert.Single(report.Results).FailureMessage);
        Assert.Contains("stop:VM-DC-01", host.Calls);
    }

    /// The heartbeat never arrives. The VM started, so something real happened and the run is
    /// not a pass — but it is also not a crash, and the report has to tell them apart.
    [Fact]
    public async Task A_vm_that_never_reports_a_heartbeat_is_reported_as_such_and_cleaned_up()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));
        host.Heartbeats.Clear();
        host.Heartbeats.Add(Heartbeat.NoContact);

        TestFailoverReport report = await Run(host, "VM-DC-01");

        Assert.Equal(TestFailoverStatus.NoHeartbeat, Assert.Single(report.Results).Status);
        Assert.Contains("stop:VM-DC-01", host.Calls);
    }

    /// A guest that cannot answer at all — an incompatible integration services version, or a
    /// paused VM. Waiting longer answers nothing, and reporting it as a pass would be the tool
    /// claiming a boot it never observed.
    [Fact]
    public async Task A_guest_that_cannot_answer_is_not_reported_as_a_pass()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));
        host.Heartbeats.Clear();
        host.Heartbeats.Add(Heartbeat.CannotConfirm);

        TestFailoverReport report = await Run(host, "VM-DC-01");

        VmTestFailoverResult result = Assert.Single(report.Results);

        Assert.Equal(TestFailoverStatus.BootedWithoutHeartbeat, result.Status);
        Assert.Contains("cannot answer", result.FailureMessage);
    }

    /// A guest with no integration services is NOT a state Hyper-V reports: status 12 means
    /// "not installed or not yet contacted" and it does not separate them. So it looks exactly
    /// like a slow boot and ends as a timeout — and the message has to admit the ambiguity
    /// rather than assert one of the two.
    [Fact]
    public async Task A_guest_with_no_integration_services_times_out_and_says_why()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));
        host.Heartbeats.Clear();
        host.Heartbeats.Add(Heartbeat.NoContact);

        TestFailoverReport report = await Run(host, "VM-DC-01");

        VmTestFailoverResult result = Assert.Single(report.Results);

        Assert.Equal(TestFailoverStatus.NoHeartbeat, result.Status);
        Assert.Contains("integration services", result.FailureMessage);
        Assert.Contains("does not distinguish", result.FailureMessage);
    }

    /// The component does not exist while the VM is not running, so an early poll says only
    /// "not up yet" and must keep waiting rather than conclude anything.
    [Fact]
    public async Task A_vm_not_yet_running_is_polled_again_rather_than_judged()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));
        host.Heartbeats.Clear();
        host.Heartbeats.AddRange([Heartbeat.NotRunning, Heartbeat.NotRunning, Heartbeat.Ok]);

        TestFailoverReport report = await Run(host, "VM-DC-01");

        Assert.Equal(TestFailoverStatus.Booted, Assert.Single(report.Results).Status);
    }

    /// The other half of the data-unavailable state, and the half that historically goes
    /// missing: the heartbeat could not be read at all. Waiting longer answers nothing, and
    /// it is no more a pass than a guest without integration services.
    [Fact]
    public async Task A_heartbeat_that_cannot_be_read_is_not_reported_as_a_pass()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));
        host.Heartbeats.Clear();
        host.Heartbeats.Add(Heartbeat.Unreadable);

        TestFailoverReport report = await Run(host, "VM-DC-01");

        VmTestFailoverResult result = Assert.Single(report.Results);

        Assert.Equal(TestFailoverStatus.BootedWithoutHeartbeat, result.Status);
        Assert.Contains("could not be read", result.FailureMessage);
        Assert.Contains("stop:VM-DC-01", host.Calls);
    }

    /// It takes three polls to come up, which is an ordinary boot rather than a failure.
    [Fact]
    public async Task A_slow_boot_is_a_pass()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));
        host.Heartbeats.Clear();
        host.Heartbeats.AddRange([Heartbeat.NoContact, Heartbeat.NoContact, Heartbeat.Ok]);

        TestFailoverReport report = await Run(host, "VM-DC-01");

        Assert.Equal(TestFailoverStatus.Booted, Assert.Single(report.Results).Status);
    }

    /// Ctrl+C. The operator interrupted on purpose, most likely because something looked
    /// wrong — which is exactly when an orphaned test VM must not be left behind.
    [Fact]
    public async Task An_interrupted_run_still_destroys_the_test_vm()
    {
        using CancellationTokenSource cancellation = new();

        // Interrupted at the worst moment: the test VM has just been created, so there is
        // something to clean up and the run is about to be told to stop.
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));
        host.TestVmFactory = name =>
        {
            cancellation.Cancel();
            return new TestVm(name + " (test copy)", null, []);
        };

        TestFailoverReport report = await Sequence(host).RunAsync(
            Plan(["VM-DC-01"]), cancellation.Token);

        Assert.Contains("stop:VM-DC-01", host.Calls);
        Assert.Equal(ExitCode.Refused, report.Code);
    }

    /// A cleanup that failed is the one outcome nobody may miss: the test VM is still there,
    /// still holding disk, and the next run will fail for a reason that looks unrelated.
    [Fact]
    public async Task A_failed_cleanup_is_reported_and_changes_the_exit_code()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start))
        {
            StopFailure = new InvalidOperationException("the test VM is locked"),
        };

        TestFailoverReport report = await Run(host, "VM-DC-01");

        VmTestFailoverResult result = Assert.Single(report.Results);

        Assert.False(result.Cleanup!.Succeeded);
        Assert.Contains("locked", result.Cleanup.FailureMessage);

        // Not 1: a reader sent to `ripcord check` would find no critical violation and
        // conclude it was transient, while a test VM quietly holds disk on the target.
        Assert.Equal(ExitCode.IntermediateState, report.Code);
    }

    /// Interrupted before the loop even starts — during the orphan scan, which is the very
    /// first host call. Nothing was touched, so the code must be 4. Letting the cancellation
    /// escape would reach the CLI's generic handler and report a local access failure, which
    /// tells a scheduler to investigate a run that did nothing at all.
    [Fact]
    public async Task An_interruption_before_anything_is_touched_still_exits_four()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));

        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        TestFailoverReport report = await Sequence(host).RunAsync(
            Plan(["VM-DC-01"]), cancellation.Token);

        Assert.Equal(ExitCode.Refused, report.Code);
        Assert.True(report.Interrupted);
        Assert.Empty(report.Results);
        Assert.DoesNotContain(host.Calls, call => call.StartsWith("create", StringComparison.Ordinal));
    }

    /// A cleanup failure outranks an interruption: "nothing changed" is simply false once a
    /// test VM has been left behind, and 4 would say exactly that.
    [Fact]
    public async Task A_failed_cleanup_outranks_an_interruption()
    {
        using CancellationTokenSource cancellation = new();

        FakeHypervProvider host = new(FakeScenarios.Healthy(Start))
        {
            StopFailure = new InvalidOperationException("the test VM is locked"),
        };

        host.TestVmFactory = name =>
        {
            cancellation.Cancel();
            return new TestVm(name + " (test copy)", null, []);
        };

        TestFailoverReport report = await Sequence(host).RunAsync(
            Plan(["VM-DC-01"]), cancellation.Token);

        Assert.Equal(ExitCode.IntermediateState, report.Code);
    }

    /// Sequentially, never in parallel: the target has about twelve gigabytes usable and a
    /// test VM consumes real memory. The second must not be created before the first is gone.
    [Fact]
    public async Task Several_vms_run_one_at_a_time()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));

        await Run(host, "VM-DC-01", "VM-LEGACY-01");

        List<string> calls = [.. host.Calls];

        Assert.True(
            calls.IndexOf("stop:VM-DC-01") < calls.IndexOf("create:VM-LEGACY-01"),
            "the second test VM was created before the first was destroyed");
    }

    /// Test VMs left behind by an earlier run are reported, because Hyper-V never mentions
    /// them and nobody looks.
    [Fact]
    public async Task Test_vms_left_behind_by_an_earlier_run_are_reported()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));
        host.ExistingTestVms.Add(new TestVm("VM-LEGACY-01 (test copy)", Start.AddDays(-3), []));

        TestFailoverReport report = await Run(host, "VM-DC-01");

        Orphan orphan = Assert.Single(report.Orphans);

        Assert.Equal("VM-LEGACY-01 (test copy)", orphan.Name);
        Assert.Equal(OrphanVerdict.Lingering, orphan.Verdict);
    }

    /// `--dry-run` is the output that gets read on the day, so it has to show the whole plan
    /// while changing nothing at all.
    [Fact]
    public async Task A_dry_run_changes_nothing()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));

        TestFailoverReport report = await Run(host, dryRun: true, vmNames: "VM-DC-01");

        Assert.True(report.DryRun);
        Assert.Equal(ExitCode.Success, report.Code);
        Assert.Equal(["VM-DC-01"], report.Results.Select(result => result.Name));
        Assert.DoesNotContain(host.Calls, call =>
            call.StartsWith("attach", StringComparison.Ordinal)
            || call.StartsWith("create", StringComparison.Ordinal)
            || call.StartsWith("start:", StringComparison.Ordinal)
            || call.StartsWith("stop:", StringComparison.Ordinal));
    }

    /// A refused precondition still reports the orphans: that is the one piece of the output
    /// that is useful precisely when the run cannot proceed.
    [Fact]
    public async Task A_refused_run_still_reports_orphans()
    {
        FakeHypervProvider host = new(FakeScenarios.Healthy(Start));
        host.ExistingTestVms.Add(new TestVm("VM-DC-01 (test copy)", Start.AddDays(-2), []));

        TestFailoverReport report = await Run(host, "VM-DC-01", check: Refusing());

        Assert.Single(report.Orphans);
    }

    private static Task<TestFailoverReport> Run(
        FakeHypervProvider host, params string[] vmNames) =>
        Run(host, false, null, vmNames);

    private static Task<TestFailoverReport> Run(
        FakeHypervProvider host, bool dryRun, params string[] vmNames) =>
        Run(host, dryRun, null, vmNames);

    private static Task<TestFailoverReport> Run(
        FakeHypervProvider host, string[] vmNames, CheckReport check) =>
        Run(host, false, check, vmNames);

    private static Task<TestFailoverReport> Run(
        FakeHypervProvider host, string vmName, CheckReport check) =>
        Run(host, false, check, [vmName]);

    private static Task<TestFailoverReport> Run(
        FakeHypervProvider host, bool dryRun, CheckReport? check, string[] vmNames) =>
        Sequence(host).RunAsync(Plan(vmNames, check, dryRun), CancellationToken.None);

    private static TestFailoverSequence Sequence(FakeHypervProvider host) =>
        new(
            host,
            new SteppingClock(Start, TimeSpan.FromSeconds(1)),
            new TestFailoverTiming(
                HeartbeatTimeout: TimeSpan.FromSeconds(5),
                PollInterval: TimeSpan.Zero,
                CleanupDeadline: TimeSpan.FromSeconds(5)));

    private static TestFailoverPlan Plan(
        IReadOnlyList<string> vmNames, CheckReport? check = null, bool dryRun = false) =>
        new(
            check ?? Passing(),
            vmNames,
            FakeScenarios.TestSwitch,
            TimeSpan.FromHours(4),
            dryRun);

    private static CheckReport Passing() =>
        Pairs.Evaluate(Pairs.Healthy(Start), Start);

    private static CheckReport Refusing() =>
        Pairs.Evaluate(
            Pairs.Healthy(Start).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { SwitchName = "vSwitch-OLD" }),
            Start);

    private static VirtualAdapter Adapter(string switchName, bool connected) =>
        new("Network Adapter", switchName, connected, "00-15-5D-01-02-01", false, null);

    /// Advances on every read, so a heartbeat deadline is reached in a bounded number of
    /// polls without any test waiting in real time.
    private sealed class SteppingClock(DateTimeOffset start, TimeSpan step) : IClock
    {
        private DateTimeOffset now = start;

        public DateTimeOffset UtcNow
        {
            get
            {
                DateTimeOffset current = this.now;
                this.now = this.now.Add(step);
                return current;
            }
        }

        public DateTimeOffset LocalNow => this.UtcNow;
    }
}
