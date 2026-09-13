using Ripcord.Domain.Failover;

namespace Ripcord.Tests.Failover;

/// Coming home. By the time it runs, `reprotect` has already put the pair back under
/// protection with the direction inverted — so this is the planned sequence pointed the other
/// way, and nothing more than that.
public class FailbackPlanTests
{
    private const string Vm = "VM-DC-01";
    private const string Home = "HV-PRIMARY-01";
    private const string Dr = "HV-REPLICA-01";

    /// The mirror image of the planned plan: the host currently serving the VM runs the first
    /// two steps, and the host it is going home to runs the rest.
    [Fact]
    public void The_host_currently_serving_the_vm_runs_the_first_two_steps()
    {
        FailoverPlan plan = FailoverPlan.Failback(Vm, home: Home, holder: Dr);

        Assert.Equal([Dr, Dr, Home, Home, Home, Home],
            plan.Steps.Select(step => step.HostName));
    }

    /// The bug this milestone was split out over. The planned sequence already reverses at its
    /// own step 4; a failback that reverses again inverts the pair and trips milestone 2's
    /// direction rule — leaving the VM at home with replication pointing the wrong way.
    [Fact]
    public void Replication_is_reversed_exactly_once()
    {
        FailoverPlan plan = FailoverPlan.Failback(Vm, home: Home, holder: Dr);

        Assert.Single(plan.Steps, step => step.Action == FailoverAction.ReverseReplication);
    }

    [Fact]
    public void The_plan_names_the_operation_it_belongs_to()
    {
        Assert.Equal(
            FailoverOperation.Failback,
            FailoverPlan.Failback(Vm, home: Home, holder: Dr).Operation);
    }

    /// Nothing to fence afterwards: both hosts are up and the pair ends the sequence under
    /// protection, which is the difference between coming home and failing over in a disaster.
    [Fact]
    public void There_is_no_host_left_to_fence()
    {
        Assert.Null(FailoverPlan.Failback(Vm, home: Home, holder: Dr).FenceOnReturn);
    }

    /// The same six steps as the planned sequence, in the same order. A failback that reordered
    /// them would be a second sequence to get right, and there is no reason for one.
    [Fact]
    public void It_is_the_planned_sequence_in_the_other_direction()
    {
        Assert.Equal(
            FailoverPlan.Planned(Vm, Dr, Home).Steps.Select(step => step.Action),
            FailoverPlan.Failback(Vm, home: Home, holder: Dr).Steps.Select(step => step.Action));
    }
}
