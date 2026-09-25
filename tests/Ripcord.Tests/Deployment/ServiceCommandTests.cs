using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Deployment;

/// `sc.exe` calls "it is already like that" a failure. Reading it as one turns a redundant
/// step into a deployment that reports itself broken on a host where nothing is wrong — and
/// the redundant step is the *expected* consequence of failing to read the running state,
/// which the observer does by design rather than guessing.
public class ServiceCommandTests
{
    [Fact]
    public void Starting_a_service_that_is_already_running_left_nothing_to_do() =>
        Assert.True(ServiceCommand.LeavesNothingToDo(
            DeploymentAction.StartService, ServiceCommand.AlreadyRunning));

    /// A restart stops and starts, so either half can report the state it was asking for.
    [Theory]
    [InlineData(ServiceCommand.AlreadyRunning)]
    [InlineData(ServiceCommand.NotRunning)]
    public void Restarting_tolerates_both_ends_of_the_same_answer(int code) =>
        Assert.True(ServiceCommand.LeavesNothingToDo(DeploymentAction.RestartService, code));

    [Fact]
    public void Removing_a_service_that_is_already_stopped_left_nothing_to_do() =>
        Assert.True(ServiceCommand.LeavesNothingToDo(
            DeploymentAction.RemoveService, ServiceCommand.NotRunning));

    [Fact]
    public void Stopping_a_service_that_is_already_stopped_left_nothing_to_do()
    {
        Assert.True(ServiceCommand.LeavesNothingToDo(
            DeploymentAction.StopService, ServiceCommand.NotRunning));
        Assert.False(ServiceCommand.LeavesNothingToDo(
            DeploymentAction.StopService, ServiceCommand.AlreadyRunning));
    }

    /// Only a running service has anything to stop, and only a stopped one anything to start.
    [Theory]
    [InlineData(true, true, DeploymentAction.StopService, null)]
    [InlineData(true, false, null, DeploymentAction.StartService)]
    [InlineData(false, false, null, null)]
    public void Stop_and_start_plan_only_what_the_state_leaves_to_do(
        bool installed, bool running, DeploymentAction? stop, DeploymentAction? start)
    {
        ObservedDeployment observed =
            ObservedDeployment.Nothing with { ServiceInstalled = installed, ServiceRunning = running };

        Assert.Equal(stop, DeploymentPlan.ToStop(observed).Steps.SingleOrDefault()?.Action);
        Assert.Equal(start, DeploymentPlan.ToStart(observed).Steps.SingleOrDefault()?.Action);
    }

    /// A start that failed for any other reason is a failure. The tolerated codes are two
    /// numbers, not a mood.
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(1053)]
    [InlineData(1060)]
    public void Any_other_code_is_a_failure(int code) =>
        Assert.False(ServiceCommand.LeavesNothingToDo(DeploymentAction.StartService, code));

    /// Scoped to the actions that start and stop: a create or an ACL change reporting one of
    /// these numbers is reporting something else, and must not be waved through by a
    /// coincidence of numbering.
    [Theory]
    [InlineData(DeploymentAction.CreateService)]
    [InlineData(DeploymentAction.UpdateService)]
    [InlineData(DeploymentAction.CreateFirewallRule)]
    [InlineData(DeploymentAction.GrantSnapshotAccess)]
    public void The_other_actions_tolerate_nothing(DeploymentAction action)
    {
        Assert.False(ServiceCommand.LeavesNothingToDo(action, ServiceCommand.AlreadyRunning));
        Assert.False(ServiceCommand.LeavesNothingToDo(action, ServiceCommand.NotRunning));
    }
}
