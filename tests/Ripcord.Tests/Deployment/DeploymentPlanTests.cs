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
        PeerAddress: "192.0.2.11",
        LogsFolder: @"D:\Ripcord\logs");

    [Fact]
    public void On_a_fresh_host_everything_is_created()
    {
        DeploymentPlan plan = DeploymentPlan.For(Desired, ObservedDeployment.Nothing);

        Assert.Equal(
            [DeploymentAction.CreateService, DeploymentAction.CreateFirewallRule,
             DeploymentAction.GrantSnapshotAccess, DeploymentAction.GrantLogsAccess,
             DeploymentAction.RegisterEventSource, DeploymentAction.StartService],
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

    /// A snapshot path naming no folder yields no folder, and says so rather than answering
    /// with the file's own path.
    ///
    /// That fallback read as harmless and was not: the executor creates this folder, so a bare
    /// `state.json` had a *directory* created exactly where the snapshot file has to be
    /// written, and every publish after it failed quietly. The configuration that would reach
    /// here is refused by the validator instead — see `ListenerSettingsValidationTests`.
    [Fact]
    public void A_snapshot_path_with_no_folder_names_no_folder() =>
        Assert.Equal("", (Desired with { SnapshotPath = "state.json" }).SnapshotFolder);

    /// Access is granted on the folder, not on the file: the snapshot is rewritten by moving a
    /// temporary file over it, which would drop an entry set on the file itself.
    [Theory]
    [InlineData(@"D:\Ripcord\state.json", @"D:\Ripcord")]
    [InlineData(@"D:\state.json", @"D:\")]
    [InlineData(@"C:\Program Files\Ripcord\state.json", @"C:\Program Files\Ripcord")]
    public void Access_is_granted_on_the_folder_holding_the_snapshot(string path, string folder)
    {
        DesiredDeployment desired = Desired with { SnapshotPath = path };

        Assert.Equal(folder, desired.SnapshotFolder);

        DeploymentStep step = Assert.Single(
            DeploymentPlan.For(desired, Matching() with { SnapshotReadableByService = false })
                .Steps);

        Assert.Contains(folder, step.Description, StringComparison.Ordinal);
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
            [DeploymentAction.RemoveEventSource, DeploymentAction.RevokeLogsAccess,
             DeploymentAction.RevokeSnapshotAccess, DeploymentAction.RemoveFirewallRule,
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

    /// Installed, pointing at the right binary, and stopped. The plan that only compared
    /// paths called this host correct, so re-running the command did nothing and the listener
    /// stayed down — while the other host reported the pair offline, which reads as a network
    /// fault rather than as a service somebody has to start.
    [Fact]
    public void A_service_that_is_installed_and_stopped_is_started()
    {
        DeploymentPlan plan = DeploymentPlan.For(
            Desired, Matching() with { ServiceRunning = false });

        DeploymentStep step = Assert.Single(plan.Steps);

        Assert.Equal(DeploymentAction.StartService, step.Action);
        Assert.Contains("not running", step.Reason, StringComparison.Ordinal);
    }

    /// Creating the service and starting it are two steps, not one. `sc create` can succeed
    /// and `sc start` fail — the service does not answer, the binary is wrong, the account
    /// cannot log on — and a single step reporting both would tell the operator nothing was
    /// done on a host that now holds a registered service.
    [Fact]
    public void A_bare_host_is_given_the_service_and_then_told_to_start_it()
    {
        DeploymentPlan plan = DeploymentPlan.For(Desired, ObservedDeployment.Nothing);

        Assert.Equal(
            [DeploymentAction.CreateService, DeploymentAction.StartService],
            plan.Steps.Select(step => step.Action).Where(action =>
                action is DeploymentAction.CreateService or DeploymentAction.StartService));

        // And the start is last of all: after the firewall rule and after the access it needs
        // to the file it serves.
        Assert.Equal(DeploymentAction.StartService, plan.Steps[^1].Action);
    }

    private static ObservedDeployment Matching() => new(
        ServiceInstalled: true,
        ServiceBinaryPath: @"D:\Ripcord\ripcord.exe",
        FirewallRuleInstalled: true,
        FirewallPort: 7443,
        FirewallRemoteAddress: "192.0.2.11",
        SnapshotReadableByService: true,
        ServiceRunning: true,
        LogsWritableByService: true,
        EventSourceRegistered: true);

    /// The field failure: the service account could write nowhere, so the listener died
    /// before it answered Windows. The grant is on the logs folder and only there — never on
    /// the install folder holding the binary.
    [Fact]
    public void A_logs_folder_the_service_cannot_write_is_granted_modify_on_that_folder_only()
    {
        DeploymentStep step = Assert.Single(
            DeploymentPlan.For(Desired, Matching() with { LogsWritableByService = false }).Steps);

        Assert.Equal(DeploymentAction.GrantLogsAccess, step.Action);
        Assert.Contains(@"'D:\Ripcord\logs'", step.Description, StringComparison.Ordinal);
        Assert.DoesNotContain(@"'D:\Ripcord'", step.Description, StringComparison.Ordinal);
        Assert.Contains("modify", step.Description, StringComparison.Ordinal);
    }

    /// Running and missing its folder: grant it, and leave the running service alone.
    [Fact]
    public void A_running_service_missing_its_logs_folder_is_granted_without_a_restart()
    {
        DeploymentPlan plan = DeploymentPlan.For(
            Desired, Matching() with { LogsWritableByService = false, EventSourceRegistered = false });

        Assert.Equal(
            [DeploymentAction.GrantLogsAccess, DeploymentAction.RegisterEventSource],
            plan.Steps.Select(step => step.Action));
    }

    /// The host as the field test left it: installed, stopped, nowhere to write. Re-running
    /// the install repairs it and only then starts the service.
    [Fact]
    public void A_stopped_service_gets_its_folder_and_source_before_it_is_started()
    {
        DeploymentPlan plan = DeploymentPlan.For(
            Desired,
            Matching() with
            {
                ServiceRunning = false,
                LogsWritableByService = false,
                EventSourceRegistered = false,
            });

        Assert.Equal(
            [DeploymentAction.GrantLogsAccess, DeploymentAction.RegisterEventSource,
             DeploymentAction.StartService],
            plan.Steps.Select(step => step.Action));
    }

    /// An update restarts the service, so it waits for the folders like a start does.
    [Fact]
    public void A_service_update_comes_after_the_grants_it_needs()
    {
        DeploymentPlan plan = DeploymentPlan.For(
            Desired,
            Matching() with
            {
                ServiceBinaryPath = @"C:\Old\ripcord.exe",
                LogsWritableByService = false,
            });

        Assert.Equal(
            [DeploymentAction.GrantLogsAccess, DeploymentAction.UpdateService],
            plan.Steps.Select(step => step.Action));
    }

    /// The logs themselves stay, like the snapshot: they explain what happened before.
    [Fact]
    public void Removal_revokes_the_logs_folder_without_deleting_it()
    {
        DeploymentStep step = Assert.Single(
            DeploymentPlan.ToRemove(ObservedDeployment.Nothing with { LogsWritableByService = true })
                .Steps);

        Assert.Equal(DeploymentAction.RevokeLogsAccess, step.Action);
        Assert.DoesNotContain("delete", step.Description, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<DeploymentAction> Actions => new(Enum.GetValues<DeploymentAction>());

    [Theory]
    [MemberData(nameof(Actions))]
    public void Only_a_start_a_restart_or_an_update_starts_the_service(DeploymentAction action) =>
        Assert.Equal(
            action is DeploymentAction.StartService
                or DeploymentAction.RestartService
                or DeploymentAction.UpdateService,
            DeploymentPlan.StartsTheService(action));
}
