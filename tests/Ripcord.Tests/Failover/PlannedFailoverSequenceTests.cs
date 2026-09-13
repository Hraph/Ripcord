using Ripcord.Adapters.Fake;
using Ripcord.Application.Failover;
using Ripcord.Domain;
using Ripcord.Domain.Failover;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Failover;

/// The sequence that moves production. Ripcord drives only the host it is running on (D29), so
/// each invocation carries out its own half and stops — and every failure path has to leave the
/// pair somewhere a human can understand, because the alternative is production down with no
/// account of why.
public class PlannedFailoverSequenceTests
{
    private const string Vm = "VM-DC-01";
    private const string Primary = "HV-PRIMARY-01";
    private const string Replica = "HV-REPLICA-01";
    private const string Switch = "vSwitch-PROD";

    private static readonly FailoverPlan Plan = FailoverPlan.Planned(Vm, Primary, Replica);

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

    private static FakeHypervProvider Provider(
        VmPowerState power, ReplicationState state = ReplicationState.Replicating) =>
        new(new HostState(
            Primary,
            [State(Vm, ReplicationRole.Primary, state, power)],
            HostReachability.Reachable()));

    private static VmReplicationState State(
        string name, ReplicationRole role, ReplicationState state, VmPowerState? power) =>
        new(
            name,
            role,
            state,
            ReplicationHealth.Normal,
            null,
            null,
            new VmFacts(
                2048, 4096, 1024,
                [new VirtualAdapter("Network Adapter", Switch, true, "00-15-5D-01", false, 10)],
                [],
                []),
            power);

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
        SplitBrain? splitBrain = null) =>
        new PlannedFailoverSequence(provider, FailoverTiming.ForTests).RunAsync(
            Plan,
            progress,
            splitBrain ?? new SplitBrain(SplitBrainVerdict.NotSuspected, null),
            new FailoverRequest(Vm, machineName, Switch, dryRun),
            CancellationToken.None);
}
