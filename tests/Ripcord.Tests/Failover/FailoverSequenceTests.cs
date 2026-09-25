using Ripcord.Adapters.Fake;
using Ripcord.Application.Failover;
using Ripcord.Domain;
using Ripcord.Domain.Audit;
using Ripcord.Domain.Failover;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Failover;

/// The sequence that moves production. Ripcord drives only the host it is running on (D29), so
/// each invocation carries out its own half and stops — and every failure path has to leave the
/// pair somewhere a human can understand, because the alternative is production down with no
/// account of why.
public class FailoverSequenceTests
{
    private const string Vm = "VM-DC-01";
    private const string Primary = "HV-PRIMARY-01";
    private const string Replica = "HV-REPLICA-01";
    private const string Switch = "vSwitch-PROD";

    private static readonly FailoverPlan Plan = FailoverPlan.Planned(Vm, Primary, Replica);

    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    /// The primary's half is steps 1 and 2. It does not attempt the replica's, and says so
    /// rather than reporting a partial success as a success.
    [Fact]
    public async Task On_the_primary_only_the_primary_steps_run()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);

        FailoverRunReport report = await Run(provider, Primary, Fresh());

        Assert.Equal([$"shutdown:{Vm}", $"prepare:{Vm}"], provider.Calls);
        Assert.Equal(ExitCode.Success, report.Code);
    }

    /// And the report names the host the operator has to move to. A sequence that stops without
    /// saying where to go next is a sequence nobody finishes.
    [Fact]
    public async Task The_report_names_the_host_that_continues_the_sequence()
    {
        FailoverRunReport report = await Run(Provider(VmPowerState.Running), Primary, Fresh());

        Assert.Contains(Replica, report.Continuation, StringComparison.Ordinal);
    }

    /// `InitiateShutdown` returns when the request is accepted. The prepare waits for the VM
    /// to read Off: Hyper-V refuses it on a running VM.
    [Fact]
    public async Task The_prepare_waits_for_the_guest_to_finish_shutting_down()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);
        provider.ShutdownCompletesAfter = 3;

        FailoverRunReport report = await Run(provider, Primary, Fresh());

        Assert.Equal(ExitCode.Success, report.Code);
        Assert.Equal(
            VmPowerState.Off,
            provider.LocalState.Vms.Single(vm => vm.Name == Vm).PowerState);
        Assert.Equal([$"shutdown:{Vm}", $"prepare:{Vm}"], provider.Calls);
    }

    /// A guest that never goes off fails step 1 on a deadline, and nothing is prepared.
    [Fact]
    public async Task A_guest_that_never_shuts_down_fails_the_step_and_prepares_nothing()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);
        provider.ShutdownCompletesAfter = null;

        FailoverRunReport report = await Run(provider, Primary, Fresh());

        Assert.Equal(ExitCode.IntermediateState, report.Code);
        Assert.DoesNotContain($"prepare:{Vm}", provider.Calls);
        Assert.Contains("was not off after", report.Steps[0].FailureMessage, StringComparison.Ordinal);
    }

    /// The guest was asked to go down, so production may be off by now: the operator is told
    /// how to bring it back or carry on, not that nothing had been done.
    [Fact]
    public async Task A_shutdown_not_confirmed_hands_the_operator_the_way_back()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);
        provider.ShutdownCompletesAfter = null;

        FailoverRunReport report = await Run(provider, Primary, Fresh());

        Assert.DoesNotContain("before anything else", report.Continuation, StringComparison.Ordinal);
        Assert.Contains($"Start-VM -Name '{Vm}'", report.ManualRecovery, StringComparison.Ordinal);
    }

    /// Run on the replica before the primary's half has happened, nothing is touched: the next
    /// step belongs to the other host, and exit 4 says nothing changed.
    [Fact]
    public async Task On_the_wrong_host_for_the_next_step_nothing_is_touched()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);

        FailoverRunReport report = await Run(provider, Replica, Fresh());

        Assert.Empty(provider.Calls);
        Assert.Equal(ExitCode.Refused, report.Code);
    }

    /// `--dry-run` prints the plan and executes nothing. That output is what gets read on the
    /// day, before anyone confirms.
    [Fact]
    public async Task A_dry_run_touches_nothing()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);

        FailoverRunReport report = await Run(provider, Primary, Fresh(), dryRun: true);

        Assert.Empty(provider.Calls);
        Assert.Equal(ExitCode.Success, report.Code);

        // Asserted non-empty first: Assert.All over an empty list passes, so a dry run that
        // returned to the operator with no plan at all would otherwise read as correct.
        Assert.NotEmpty(report.Steps);
        Assert.All(report.Steps, step => Assert.Equal(StepOutcome.Planned, step.Outcome));
    }

    /// The shutdown succeeded and the prepare failed, so production is off with no failover
    /// started. Leaving it there is an outage Ripcord caused; the VM goes back on.
    [Fact]
    public async Task A_failed_prepare_starts_the_vm_again()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);
        provider.PrepareFailure = new InvalidOperationException("prepare refused");

        FailoverRunReport report = await Run(provider, Primary, Fresh());

        Assert.Equal([$"shutdown:{Vm}", $"prepare:{Vm}", $"start:{Vm}"], provider.Calls);
        Assert.True(report.Rollback!.Succeeded);
    }

    /// A step that was attempted and failed leaves the pair somewhere nobody chose, even when
    /// the undo worked: exit 5 is the code that says a human has to look, and it must never be
    /// softened to 4, which asserts that nothing changed.
    [Fact]
    public async Task An_attempted_step_that_failed_exits_five_even_when_the_undo_worked()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);
        provider.PrepareFailure = new InvalidOperationException("prepare refused");

        FailoverRunReport report = await Run(provider, Primary, Fresh());

        Assert.Equal(ExitCode.IntermediateState, report.Code);
    }

    /// The undo itself failing is the case this milestone exists to handle well: production is
    /// off and Ripcord cannot put it back. It must say so unmissably and name the command.
    [Fact]
    public async Task A_failed_undo_names_the_command_the_operator_must_run()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);
        provider.PrepareFailure = new InvalidOperationException("prepare refused");
        provider.StartRealVmFailure = new InvalidOperationException("start refused");

        FailoverRunReport report = await Run(provider, Primary, Fresh());

        Assert.False(report.Rollback!.Succeeded);
        Assert.Equal(ExitCode.IntermediateState, report.Code);
        Assert.Contains(Vm, report.ManualRecovery!, StringComparison.Ordinal);
    }

    /// The undo is retried, because a single bounded attempt is the right policy for discarding
    /// a copy and the wrong one for putting production back.
    [Fact]
    public async Task The_undo_is_attempted_more_than_once_before_giving_up()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);
        provider.PrepareFailure = new InvalidOperationException("prepare refused");
        provider.StartRealVmFailure = new InvalidOperationException("start refused");

        await Run(provider, Primary, Fresh());

        Assert.True(
            provider.Calls.Count(call => call == $"start:{Vm}") > 1,
            "the restore policy must retry, unlike milestone 3's discard policy");
    }

    /// A failed reverse, where the failover itself ran in an *earlier* invocation. Ripcord
    /// undoes only what this invocation did, so it does not cancel a failover the operator
    /// deliberately performed before: silently discarding somebody else's completed step is the
    /// unilateral destructive act this whole milestone is built to avoid. It reports the state
    /// and stops.
    [Fact]
    public async Task A_failed_reverse_does_not_discard_a_failover_from_an_earlier_run()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Off, ReplicationState.Recovered);
        provider.ReverseFailure = new InvalidOperationException("reverse refused");

        FailoverRunReport report = await Run(provider, Replica, AfterFailover());

        Assert.DoesNotContain($"cancel:{Vm}", provider.Calls);
        Assert.Equal(ExitCode.IntermediateState, report.Code);
    }

    /// **The cross-host handoff does not work yet, and this test pins that rather than hiding
    /// it.** The primary's prepare leaves no state this binary can name (V35), so once the
    /// primary has run steps 1 and 2, the replica still reads step 2 as outstanding — and step 2
    /// is not its to run. The sequence refuses rather than proceeding on an assumption.
    ///
    /// Refusing is the safe direction and the honest one: the alternative is the replica
    /// attempting a failover whose precondition it cannot see. Closing this needs either V35
    /// answered from the lab, or the operator explicitly asserting that the primary's half is
    /// done — an assertion a human witnessed, which is not the same as a cursor Ripcord wrote
    /// down and later believed.
    [Fact]
    public async Task The_replica_cannot_yet_observe_that_the_primary_prepared()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Off);

        FailoverProgress afterShutdown = FailoverProgress.Of(
            Plan,
            State(Vm, ReplicationRole.Primary, ReplicationState.Replicating, VmPowerState.Off),
            State(Vm, ReplicationRole.Replica, ReplicationState.Replicating, VmPowerState.Off));

        FailoverRunReport report = await Run(provider, Replica, afterShutdown);

        Assert.Empty(provider.Calls);
        Assert.Equal(ExitCode.Refused, report.Code);
        Assert.Contains(Primary, report.Continuation, StringComparison.Ordinal);
    }

    /// Split brain halts before anything is attempted. There is no sequence that recovers from
    /// two live claimants, so running one more step can only make it worse.
    [Fact]
    public async Task A_suspected_split_brain_halts_before_anything_runs()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);

        FailoverRunReport report = await Run(
            provider,
            Primary,
            Fresh(),
            splitBrain: new SplitBrain(SplitBrainVerdict.Suspected, "both hosts claim it"));

        Assert.Empty(provider.Calls);
        Assert.Equal(ExitCode.Refused, report.Code);
        Assert.NotNull(report.Halt);
    }

    /// A sequence that cannot work out where it is does not guess. Nothing is attempted, and
    /// nothing has changed, so this is a refusal rather than an intermediate state.
    [Fact]
    public async Task A_sequence_that_cannot_be_placed_refuses_without_touching_anything()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);

        FailoverProgress unplaceable = FailoverProgress.Of(
            Plan, State(Vm, ReplicationRole.Primary, ReplicationState.Replicating, null), null);

        FailoverRunReport report = await Run(provider, Primary, unplaceable);

        Assert.Empty(provider.Calls);
        Assert.Equal(ExitCode.Refused, report.Code);
    }

    /// Step 6 actually running, on a VM whose adapter is connected to the expected switch.
    /// Every other test stops before this step; the milestone calls it "not a formality".
    [Fact]
    public async Task The_network_check_passes_when_the_adapter_is_connected_to_the_switch()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);

        FailoverRunReport report = await Run(provider, Replica, AfterStart());

        Assert.Equal(ExitCode.Success, report.Code);
    }

    /// A VM that boots without network is a failed failover, and the host-level checks all
    /// passed while that was true.
    [Fact]
    public async Task A_disconnected_adapter_fails_the_network_check()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running, connected: false);

        FailoverRunReport report = await Run(provider, Replica, AfterStart());

        Assert.Equal(ExitCode.CriticalFinding, report.Code);
    }

    /// Connected, but to something else. Bound to the right switch and disconnected, and bound
    /// to the wrong switch while connected, are two different ways to have no production
    /// network — checking only one of them is the proxy trap.
    [Fact]
    public async Task An_adapter_on_the_wrong_switch_fails_the_network_check()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running, switchName: "vSwitch-DMZ");

        FailoverRunReport report = await Run(provider, Replica, AfterStart());

        Assert.Equal(ExitCode.CriticalFinding, report.Code);
    }

    /// The network could not be read at all. That is not a pass: it is reported as unverified,
    /// because a green line here would say the failover was confirmed when nothing was.
    [Fact]
    public async Task An_unreadable_network_is_not_treated_as_a_working_one()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running, facts: false);

        FailoverRunReport report = await Run(provider, Replica, AfterStart());

        Assert.Equal(ExitCode.CriticalFinding, report.Code);
    }

    /// **No rollback here.** The VM is up and serving; step 6 is a verification, and tearing a
    /// live workload down because a check failed would cause the outage the check exists to
    /// warn about. Fix the adapter on this host instead.
    [Fact]
    public async Task A_failed_network_check_does_not_unwind_a_running_vm()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running, connected: false);

        FailoverRunReport report = await Run(provider, Replica, AfterStart());

        Assert.DoesNotContain($"cancel:{Vm}", provider.Calls);
        Assert.Null(report.Rollback);
    }

    /// The ordering is the guarantee. Once a step has run the host may not be writable, so an
    /// operation that recorded only its outcome would record nothing in exactly the case the
    /// record exists for.
    [Fact]
    public async Task What_could_not_be_seen_is_recorded_before_anything_changes()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);
        InMemoryAuditLog audit = new();
        int callsAtFirstWrite = -1;

        audit.OnAppend = _ =>
            callsAtFirstWrite = callsAtFirstWrite < 0 ? provider.Calls.Count : callsAtFirstWrite;

        await Run(provider, Primary, Fresh(), audit: audit);

        Assert.Equal(0, callsAtFirstWrite);
        Assert.Equal(AuditStage.Starting, audit.Entries[0].Stage);
    }

    /// The unknowns the precondition decided to proceed past travel into the trail, because a
    /// failover that proceeds on unknowns owes a durable account of which ones.
    [Fact]
    public async Task The_unverified_facts_are_carried_into_the_trail()
    {
        InMemoryAuditLog audit = new();

        await new FailoverSequence(
                Provider(VmPowerState.Running), audit, new FixedClock(Now),
                FailoverTiming.ForTests)
            .RunAsync(
                Plan,
                Fresh(),
                new SplitBrain(SplitBrainVerdict.NotSuspected, null),
                new FailoverRequest(
                    Vm, Primary, Switch, false, "RH", ["free space could not be read"]),
                CancellationToken.None);

        Assert.Contains(
            "free space could not be read", audit.Entries[0].Unverified);
    }

    /// A failover that cannot record what it knew beforehand does not start. The audit trail is
    /// not decoration on a mutating operation; it is the account of the state production was
    /// moved from.
    [Fact]
    public async Task A_trail_that_cannot_be_written_stops_the_failover()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);
        InMemoryAuditLog audit = new() { Failure = new IOException("read-only") };

        await Assert.ThrowsAsync<IOException>(
            () => Run(provider, Primary, Fresh(), audit: audit));

        Assert.Empty(provider.Calls);
    }

    /// Both ends are recorded, and the failing run is the one somebody reads.
    [Fact]
    public async Task The_outcome_is_recorded_as_well_as_the_intent()
    {
        InMemoryAuditLog audit = new();

        await Run(Provider(VmPowerState.Running), Primary, Fresh(), audit: audit);

        Assert.Equal(
            [AuditStage.Starting, AuditStage.Finished],
            audit.Entries.Select(entry => entry.Stage));
    }

    /// The outcome is written whatever the outcome was, and this is the run somebody actually
    /// reads: production went off, the failover did not take, and the trail has to say so with
    /// the exit code that means a human must look.
    [Fact]
    public async Task The_trail_records_the_outcome_of_a_run_that_failed()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Running);
        provider.PrepareFailure = new InvalidOperationException("prepare refused");

        InMemoryAuditLog audit = new();

        FailoverRunReport report = await Run(provider, Primary, Fresh(), audit: audit);

        Assert.Equal(ExitCode.IntermediateState, report.Code);

        Assert.Equal(
            [AuditStage.Starting, AuditStage.Finished],
            audit.Entries.Select(entry => entry.Stage));

        Assert.Contains(
            $"exit {(int)ExitCode.IntermediateState}",
            audit.Entries[1].Detail,
            StringComparison.Ordinal);
    }

    /// `--dry-run` changed nothing, so there is nothing to account for. A trail full of
    /// rehearsals is a trail nobody reads on the day.
    [Fact]
    public async Task A_dry_run_writes_no_audit_entry()
    {
        InMemoryAuditLog audit = new();

        await Run(Provider(VmPowerState.Running), Primary, Fresh(), dryRun: true, audit: audit);

        Assert.Empty(audit.Entries);
    }

    /// The disaster path, on the host that is still alive. Three steps, all here, and the
    /// operator is not sent anywhere afterwards — there is nowhere to go.
    [Fact]
    public async Task An_unplanned_run_does_this_hosts_three_steps_and_finishes()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Off);

        FailoverRunReport report = await Run(
            provider, Replica, UnplannedFresh(), plan: UnplannedPlan);

        Assert.Equal([$"failover:{Vm}", $"start:{Vm}"], provider.Calls);
        Assert.Equal(ExitCode.Success, report.Code);
    }

    /// Nothing is undone when the start fails after the failover has taken, and the absence is
    /// the decision. Cancelling now would discard the recovery point the failover just brought
    /// up and leave production down — the outage this command was typed to end. So the VM stays
    /// failed over here and the operator is handed the command to start it.
    [Fact]
    public async Task An_unplanned_run_does_not_undo_the_failover_when_the_start_fails()
    {
        FakeHypervProvider provider = Provider(VmPowerState.Off);
        provider.StartRealVmFailure = new InvalidOperationException("no capacity");

        FailoverRunReport report = await Run(
            provider, Replica, UnplannedFresh(), plan: UnplannedPlan);

        Assert.DoesNotContain($"cancel:{Vm}", provider.Calls);
        Assert.Equal(ExitCode.IntermediateState, report.Code);
        Assert.Contains("Start-VM", report.ManualRecovery);
        Assert.Null(report.Rollback);
    }

    /// The trail has to name the operation that ran. Half a sequence executed by each host is
    /// read back afterwards by somebody working out what happened, and "planned" against an
    /// unplanned failover is the one word that would mislead them.
    [Fact]
    public async Task The_trail_names_the_scenario_that_ran()
    {
        InMemoryAuditLog audit = new();

        await Run(
            Provider(VmPowerState.Off), Replica, UnplannedFresh(),
            audit: audit, plan: UnplannedPlan);

        Assert.Equal("failover --scenario unplanned", audit.Entries[0].Operation);
    }

    private static readonly FailoverPlan UnplannedPlan =
        FailoverPlan.Unplanned(Vm, Primary, Replica);

    /// The primary reports nothing at all, which is the shape of the scenario: it is gone.
    private static FailoverProgress UnplannedFresh() =>
        FailoverProgress.Of(
            UnplannedPlan,
            null,
            State(Vm, ReplicationRole.Replica, ReplicationState.Replicating, VmPowerState.Off));

    private static FakeHypervProvider Provider(
        VmPowerState power,
        ReplicationState state = ReplicationState.Replicating,
        bool connected = true,
        string switchName = Switch,
        bool facts = true) =>
        new(new HostState(
            Primary,
            [State(Vm, ReplicationRole.Primary, state, power, connected, switchName, facts)],
            HostReachability.Reachable()));

    private static VmReplicationState State(
        string name,
        ReplicationRole role,
        ReplicationState state,
        VmPowerState? power,
        bool connected = true,
        string switchName = Switch,
        bool facts = true) =>
        new(
            name,
            role,
            state,
            ReplicationHealth.Normal,
            null,
            null,
            facts
                ? new VmFacts(
                    2048, 4096, 1024,
                    [new VirtualAdapter(
                        "Network Adapter", switchName, connected, "00-15-5D-01", false, 10)],
                    [],
                    [])
                : null,
            power);

    /// Steps 1 to 5 behind it: the target is serving the VM and it is running, so only the
    /// network check is left.
    private static FailoverProgress AfterStart() =>
        FailoverProgress.Of(
            Plan,
            State(Vm, ReplicationRole.Replica, ReplicationState.Replicating, VmPowerState.Off),
            State(Vm, ReplicationRole.Primary, ReplicationState.Recovered, VmPowerState.Running));

    private static FailoverProgress Fresh() =>
        FailoverProgress.Of(
            Plan,
            State(Vm, ReplicationRole.Primary, ReplicationState.Replicating, VmPowerState.Running),
            State(Vm, ReplicationRole.Replica, ReplicationState.Replicating, VmPowerState.Off));

    private static FailoverProgress AfterFailover() =>
        FailoverProgress.Of(
            Plan,
            State(Vm, ReplicationRole.Primary, ReplicationState.Recovered, VmPowerState.Off),
            State(Vm, ReplicationRole.Replica, ReplicationState.Recovered, VmPowerState.Off));

    private static Task<FailoverRunReport> Run(
        FakeHypervProvider provider,
        string machineName,
        FailoverProgress progress,
        bool dryRun = false,
        SplitBrain? splitBrain = null,
        InMemoryAuditLog? audit = null,
        FailoverPlan? plan = null) =>
        new FailoverSequence(
            provider, audit ?? new InMemoryAuditLog(), new FixedClock(Now), FailoverTiming.ForTests)
        .RunAsync(
            plan ?? Plan,
            progress,
            splitBrain ?? new SplitBrain(SplitBrainVerdict.NotSuspected, null),
            new FailoverRequest(Vm, machineName, Switch, dryRun),
            CancellationToken.None);
}
