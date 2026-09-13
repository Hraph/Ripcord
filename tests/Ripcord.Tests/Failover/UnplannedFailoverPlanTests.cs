using Ripcord.Domain.Failover;

namespace Ripcord.Tests.Failover;

/// The disaster path. Everything the planned sequence does on the primary is unavailable here
/// by definition — that host is why this command is being typed — so the whole plan runs on
/// the replica and stops short of anything the dead host has to agree to.
public class UnplannedFailoverPlanTests
{
    private const string Vm = "VM-DC-01";
    private const string Primary = "HV-PRIMARY-01";
    private const string Replica = "HV-REPLICA-01";

    [Fact]
    public void The_three_steps_all_run_on_the_replica()
    {
        FailoverPlan plan = FailoverPlan.Unplanned(Vm, Primary, Replica);

        Assert.Equal(
            [FailoverAction.StartFailover, FailoverAction.StartVm, FailoverAction.VerifyNetwork],
            plan.Steps.Select(step => step.Action));

        Assert.All(plan.Steps, step => Assert.Equal(Replica, step.HostName));
    }

    /// There is nothing to shut down and nothing to send: the primary is gone. A plan that
    /// listed either would stall on the first step for ever, on the one path where stalling
    /// means production stays down.
    [Fact]
    public void Nothing_is_asked_of_the_primary()
    {
        FailoverPlan plan = FailoverPlan.Unplanned(Vm, Primary, Replica);

        Assert.DoesNotContain(plan.Steps, step => step.RunsOn(Primary));

        Assert.DoesNotContain(
            plan.Steps,
            step => step.Action is FailoverAction.ShutDownVm or FailoverAction.PrepareFailover);
    }

    /// Replication is **not** reversed here, and the absence is the decision. Reversing needs
    /// the original primary to accept the new direction, and it is not there to accept
    /// anything; putting the pair back under protection is `reprotect`, run when it returns.
    [Fact]
    public void Replication_is_not_reversed()
    {
        FailoverPlan plan = FailoverPlan.Unplanned(Vm, Primary, Replica);

        Assert.DoesNotContain(
            plan.Steps, step => step.Action == FailoverAction.ReverseReplication);
    }

    [Fact]
    public void Steps_are_numbered_from_one_in_order()
    {
        FailoverPlan plan = FailoverPlan.Unplanned(Vm, Primary, Replica);

        Assert.Equal([1, 2, 3], plan.Steps.Select(step => step.Number));
    }

    [Fact]
    public void Every_step_describes_itself()
    {
        FailoverPlan plan = FailoverPlan.Unplanned(Vm, Primary, Replica);

        Assert.All(plan.Steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Description)));
    }

    /// The plan says which operation it is, so the audit trail, the precondition and the
    /// rollback policy all read it from one place rather than each being told separately.
    [Fact]
    public void The_plan_names_the_operation_it_belongs_to()
    {
        Assert.Equal(
            FailoverOperation.UnplannedFailover,
            FailoverPlan.Unplanned(Vm, Primary, Replica).Operation);

        Assert.Equal(
            FailoverOperation.PlannedFailover,
            FailoverPlan.Planned(Vm, Primary, Replica).Operation);
    }
}
