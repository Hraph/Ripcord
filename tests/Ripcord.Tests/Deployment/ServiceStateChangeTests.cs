using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Deployment;

/// What `service start` and `stop` do, from the state Windows reports. A state that could not
/// be read is never taken for a stopped one.
public class ServiceStateChangeTests
{
    private static readonly ObservedDeployment Installed =
        ObservedDeployment.Nothing with { ServiceInstalled = true };

    [Theory]
    [InlineData(ServiceChange.Stop, ServiceRunState.Running, DeploymentAction.StopService)]
    [InlineData(ServiceChange.Stop, ServiceRunState.StartPending, DeploymentAction.StopService)]
    [InlineData(ServiceChange.Stop, ServiceRunState.Paused, DeploymentAction.StopService)]
    [InlineData(ServiceChange.Start, ServiceRunState.Stopped, DeploymentAction.StartService)]
    public void Planned_only_where_there_is_something_to_do(
        ServiceChange change, ServiceRunState state, DeploymentAction expected)
    {
        ServiceStateChange decided = ServiceStateChange.For(change, In(state), Installed);

        Assert.Equal(expected, Assert.Single(decided.Plan.Steps).Action);
        Assert.Null(decided.Unchanged);
        Assert.Null(decided.Unreadable);
    }

    [Theory]
    [InlineData(ServiceChange.Stop, ServiceRunState.Stopped, "is not running")]
    [InlineData(ServiceChange.Stop, ServiceRunState.StopPending, "is already stopping")]
    [InlineData(ServiceChange.Start, ServiceRunState.Running, "is already running")]
    [InlineData(ServiceChange.Start, ServiceRunState.StartPending, "is already starting")]
    [InlineData(ServiceChange.Start, ServiceRunState.StopPending, "is stopping")]
    [InlineData(ServiceChange.Start, ServiceRunState.Paused, "is paused")]
    public void Nothing_to_do_is_said_and_plans_nothing(
        ServiceChange change, ServiceRunState state, string said)
    {
        ServiceStateChange decided = ServiceStateChange.For(change, In(state), Installed);

        Assert.Empty(decided.Plan.Steps);
        Assert.Contains(said, decided.Unchanged, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was changed.", decided.Unchanged, StringComparison.Ordinal);
    }

    /// The review's finding: an unreadable state used to be told "not running".
    [Theory]
    [InlineData(ServiceChange.Start)]
    [InlineData(ServiceChange.Stop)]
    public void An_unreadable_state_is_refused_with_its_reason(ServiceChange change)
    {
        ServiceStateChange decided = ServiceStateChange.For(
            change, In(ServiceRunState.Unknown) with { Unreadable = ObservedService.AccessDenied }, Installed);

        Assert.Empty(decided.Plan.Steps);
        Assert.Null(decided.Unchanged);
        Assert.Contains("access is denied", decided.Unreadable, StringComparison.Ordinal);
    }

    private static ObservedService In(ServiceRunState state) =>
        new(true, "\"C:\\Program Files\\Ripcord\\ripcord.exe\" serve", state, "Auto", 0, 0);
}
