using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Deployment;

/// Installing is the first thing Ripcord does that is not copying a file, so it is also the
/// first thing that has to be reversible and repeatable. The plan is a pure function of
/// "what is there" and "what should be there"; only carrying it out touches Windows.
public class DeploymentPlanTests
{
    private static readonly DesiredDeployment Desired = new(
        BinaryPath: @"D:\Ripcord\ripcord.exe",
        SnapshotPath: @"D:\Ripcord\state.json",
        Port: 7443,
        PeerAddress: "192.0.2.11");

    [Fact]
    public void On_a_fresh_host_everything_is_created()
    {
        DeploymentPlan plan = DeploymentPlan.For(Desired, ObservedDeployment.Nothing);

        Assert.Equal(
            [DeploymentAction.CreateService, DeploymentAction.CreateFirewallRule,
             DeploymentAction.GrantSnapshotAccess],
            plan.Steps.Select(step => step.Action));
        Assert.True(plan.ChangesAnything);
    }

    /// Re-running a deployment that is already correct must do nothing at all. An installer
    /// that reinstalls every time is one nobody dares run twice.
    [Fact]
    public void A_host_already_deployed_correctly_needs_no_steps()
    {
        DeploymentPlan plan = DeploymentPlan.For(Desired, Matching());

        Assert.Empty(plan.Steps);
        Assert.False(plan.ChangesAnything);
    }

    /// The migration case: the binary moved, or the config changed the port or the peer. The
    /// existing service is reconfigured rather than left pointing at the old one.
    [Fact]
    public void A_service_pointing_at_the_wrong_binary_is_updated_not_recreated()
    {
        ObservedDeployment observed = Matching() with
        {
            ServiceBinaryPath = @"C:\Old\ripcord.exe",
        };

        DeploymentPlan plan = DeploymentPlan.For(Desired, observed);

        DeploymentStep step = Assert.Single(plan.Steps);
        Assert.Equal(DeploymentAction.UpdateService, step.Action);
        Assert.Contains(@"C:\Old\ripcord.exe", step.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_firewall_rule_on_the_wrong_port_is_updated()
    {
        DeploymentPlan plan = DeploymentPlan.For(Desired, Matching() with { FirewallPort = 8443 });

        Assert.Equal(DeploymentAction.UpdateFirewallRule, Assert.Single(plan.Steps).Action);
    }

    /// The rule exists but lets anyone in. That is the whole point of the rule, so it counts
    /// as wrong rather than as merely different.
    [Fact]
    public void A_firewall_rule_open_to_the_wrong_address_is_updated()
    {
        DeploymentPlan plan = DeploymentPlan.For(
            Desired, Matching() with { FirewallRemoteAddress = "any" });

        DeploymentStep step = Assert.Single(plan.Steps);
        Assert.Equal(DeploymentAction.UpdateFirewallRule, step.Action);
        Assert.Contains("any", step.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_snapshot_file_the_service_cannot_read_is_regranted()
    {
        DeploymentPlan plan = DeploymentPlan.For(
            Desired, Matching() with { SnapshotReadableByService = false });

        Assert.Equal(DeploymentAction.GrantSnapshotAccess, Assert.Single(plan.Steps).Action);
    }

    /// Removal is the same function with nothing desired, so an uninstaller cannot drift from
    /// the installer: they are one list read in two directions.
    [Fact]
    public void Removal_undoes_exactly_what_was_installed_in_reverse_order()
    {
        DeploymentPlan plan = DeploymentPlan.ToRemove(Matching());

        Assert.Equal(
            [DeploymentAction.RevokeSnapshotAccess, DeploymentAction.RemoveFirewallRule,
             DeploymentAction.RemoveService],
            plan.Steps.Select(step => step.Action));
    }

    [Fact]
    public void Removing_from_a_host_with_nothing_installed_is_a_no_op()
    {
        DeploymentPlan plan = DeploymentPlan.ToRemove(ObservedDeployment.Nothing);

        Assert.Empty(plan.Steps);
        Assert.False(plan.ChangesAnything);
    }

    /// Removal leaves the snapshot file behind: it holds no secret, and deleting data on the
    /// way out is how an uninstaller becomes something people are afraid to run.
    [Fact]
    public void Removal_never_deletes_the_snapshot_file_itself()
    {
        DeploymentPlan plan = DeploymentPlan.ToRemove(Matching());

        Assert.DoesNotContain(
            plan.Steps, step => step.Reason.Contains("delete", StringComparison.OrdinalIgnoreCase));
    }

    /// The service is created before the port is opened and removed after it is closed, so
    /// there is never a moment when the port is open onto nothing.
    [Fact]
    public void The_port_is_never_open_without_a_service_behind_it()
    {
        List<DeploymentAction> install =
            [.. DeploymentPlan.For(Desired, ObservedDeployment.Nothing).Steps
                .Select(step => step.Action)];

        List<DeploymentAction> remove =
            [.. DeploymentPlan.ToRemove(Matching()).Steps.Select(step => step.Action)];

        Assert.True(
            install.IndexOf(DeploymentAction.CreateService)
            < install.IndexOf(DeploymentAction.CreateFirewallRule));

        Assert.True(
            remove.IndexOf(DeploymentAction.RemoveFirewallRule)
            < remove.IndexOf(DeploymentAction.RemoveService));
    }

    /// Every step says what it will do, in the operator's terms, before anything happens.
    [Fact]
    public void Every_step_states_what_it_changes_and_why()
    {
        foreach (DeploymentStep step in DeploymentPlan.For(Desired, ObservedDeployment.Nothing).Steps)
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Description));
            Assert.False(string.IsNullOrWhiteSpace(step.Reason));
        }
    }

    private static ObservedDeployment Matching() => new(
        ServiceInstalled: true,
        ServiceBinaryPath: @"D:\Ripcord\ripcord.exe",
        FirewallRuleInstalled: true,
        FirewallPort: 7443,
        FirewallRemoteAddress: "192.0.2.11",
        SnapshotReadableByService: true);
}
