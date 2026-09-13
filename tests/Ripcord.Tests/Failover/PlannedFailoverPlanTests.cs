using Ripcord.Domain.Failover;

namespace Ripcord.Tests.Failover;

/// The plan is what `--dry-run` prints, and that output is what gets read on the day before
/// anyone confirms. Every step names the host it runs on: leaving that implicit is how a
/// sequence gets encoded backwards, and a sequence encoded backwards fails over the wrong way
/// with production already shut down.
public class PlannedFailoverPlanTests
{
    private const string Vm = "VM-DC-01";
    private const string Primary = "HV-PRIMARY-01";
    private const string Replica = "HV-REPLICA-01";

    [Fact]
    public void The_documented_six_steps_appear_in_order()
    {
        FailoverPlan plan = FailoverPlan.Planned(Vm, Primary, Replica);

        Assert.Equal(
            [
                FailoverAction.ShutDownVm,
                FailoverAction.PrepareFailover,
                FailoverAction.StartFailover,
                FailoverAction.ReverseReplication,
                FailoverAction.StartVm,
                FailoverAction.VerifyNetwork,
            ],
            plan.Steps.Select(step => step.Action));
    }

    /// The first two run where the VM still is; the rest run where it is going. Getting this
    /// boundary wrong is the failure the host column exists to prevent.
    [Fact]
    public void The_first_two_steps_run_on_the_primary_and_the_rest_on_the_replica()
    {
        FailoverPlan plan = FailoverPlan.Planned(Vm, Primary, Replica);

        Assert.Equal([Primary, Primary, Replica, Replica, Replica, Replica],
            plan.Steps.Select(step => step.HostName));
    }

    [Fact]
    public void Every_step_names_a_host()
    {
        FailoverPlan plan = FailoverPlan.Planned(Vm, Primary, Replica);

        Assert.All(plan.Steps, step => Assert.False(string.IsNullOrWhiteSpace(step.HostName)));
    }

    /// The planned sequence already reverses replication at its own step. A failback that
    /// reverses again inverts the pair and trips milestone 2's direction rule — a bug in the
    /// sequence as first written, and one that would be invisible without this assertion.
    [Fact]
    public void Replication_is_reversed_exactly_once()
    {
        FailoverPlan plan = FailoverPlan.Planned(Vm, Primary, Replica);

        Assert.Single(plan.Steps, step => step.Action == FailoverAction.ReverseReplication);
    }

    /// Ripcord drives only the host it runs on (D29). The plan therefore has to say which
    /// steps this invocation can carry out and which the operator runs on the other side.
    [Fact]
    public void The_plan_marks_which_steps_this_host_can_run()
    {
        FailoverPlan plan = FailoverPlan.Planned(Vm, Primary, Replica);

        Assert.Equal(
            [true, true, false, false, false, false],
            plan.Steps.Select(step => step.RunsOn(Primary)));
    }

    /// Run from the other side, the same plan flips which half is local. One plan, read from
    /// two hosts, rather than two plans that can disagree.
    [Fact]
    public void The_same_plan_read_from_the_replica_marks_the_other_half()
    {
        FailoverPlan plan = FailoverPlan.Planned(Vm, Primary, Replica);

        Assert.Equal(
            [false, false, true, true, true, true],
            plan.Steps.Select(step => step.RunsOn(Replica)));
    }

    /// Host names are compared case-insensitively: Windows host names are, and the
    /// configuration was typed by hand.
    [Fact]
    public void A_host_name_matches_regardless_of_case()
    {
        FailoverPlan plan = FailoverPlan.Planned(Vm, Primary, Replica);

        Assert.True(plan.Steps[0].RunsOn(Primary.ToLowerInvariant()));
    }

    /// A step nobody can read under pressure is a step that gets skipped. Each one says what
    /// it does, not which cmdlet it maps to.
    [Fact]
    public void Every_step_describes_itself()
    {
        FailoverPlan plan = FailoverPlan.Planned(Vm, Primary, Replica);

        Assert.All(plan.Steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Description)));
    }

    /// Steps are numbered from one, in the order they run, so the report and the operator are
    /// talking about the same step 3.
    [Fact]
    public void Steps_are_numbered_from_one_in_order()
    {
        FailoverPlan plan = FailoverPlan.Planned(Vm, Primary, Replica);

        Assert.Equal([1, 2, 3, 4, 5, 6], plan.Steps.Select(step => step.Number));
    }

    /// The plan carries the VM it is about: three of these get printed one after another
    /// during a sweep, and an unlabelled plan is unreadable.
    [Fact]
    public void The_plan_names_its_vm()
    {
        Assert.Equal(Vm, FailoverPlan.Planned(Vm, Primary, Replica).VmName);
    }
}
