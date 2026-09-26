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
        ServiceStateChange decided = ServiceStateChange.For(change, In(state));

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
        ServiceStateChange decided = ServiceStateChange.For(change, In(state));

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
            change, In(ServiceRunState.Unknown) with { Unreadable = ObservedService.AccessDenied });

        Assert.Empty(decided.Plan.Steps);
        Assert.Null(decided.Unchanged);
        Assert.Contains("access is denied", decided.Unreadable, StringComparison.Ordinal);
    }

    /// Started listener first, so the peer is served before it is refreshed; stopped publisher
    /// first, so nothing is written for a listener that no longer serves.
    [Theory]
    [InlineData(ServiceChange.Start, ServiceRunState.Stopped, false)]
    [InlineData(ServiceChange.Restart, ServiceRunState.Running, false)]
    [InlineData(ServiceChange.Stop, ServiceRunState.Running, true)]
    public void Both_services_are_changed_in_the_order_that_keeps_the_peer_served(
        ServiceChange change, ServiceRunState state, bool publisherFirst)
    {
        ServiceStateChange decided = ServiceStateChange.ForBoth(change, In(state), In(state));

        Assert.Equal(2, decided.Plan.Steps.Count);
        Assert.Equal(
            publisherFirst ? RipcordService.Publisher : RipcordService.Listener,
            decided.Plan.Steps[0].Service);
    }

    [Fact]
    public void A_publisher_not_installed_is_said_and_the_listener_still_acted_on()
    {
        ServiceStateChange decided = ServiceStateChange.ForBoth(
            ServiceChange.Start, In(ServiceRunState.Stopped), ObservedService.Absent);

        Assert.Equal(RipcordService.Listener, Assert.Single(decided.Plan.Steps).Service);
        Assert.Contains("'ripcord-publish' service is not installed", decided.Unchanged, StringComparison.Ordinal);

        // The listener is started: saying nothing changed would be the one false line.
        Assert.DoesNotContain("Nothing was changed", decided.Unchanged, StringComparison.Ordinal);
    }

    [Fact]
    public void Either_state_unreadable_changes_neither()
    {
        ServiceStateChange decided = ServiceStateChange.ForBoth(
            ServiceChange.Stop,
            In(ServiceRunState.Running),
            In(ServiceRunState.Unknown) with { Unreadable = ObservedService.AccessDenied });

        Assert.Empty(decided.Plan.Steps);
        Assert.NotNull(decided.Unreadable);
    }

    private static ObservedService In(ServiceRunState state) =>
        new(true, "\"C:\\Program Files\\Ripcord\\ripcord.exe\" serve", state, "Auto", 0, 0);
}
