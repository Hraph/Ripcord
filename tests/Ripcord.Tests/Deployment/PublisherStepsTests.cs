using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Deployment;

/// The publishing service in `service install` and `service remove`: least privilege, step by
/// step, and everything it was given taken back before its service goes.
public class PublisherStepsTests
{
    private const string Binary = @"C:\Program Files\Ripcord\ripcord.exe";

    private static readonly DesiredDeployment Desired = new(
        BinaryPath: Binary,
        SnapshotPath: @"C:\Program Files\Ripcord\state\state.json",
        Port: 7443,
        PeerAddress: "192.0.2.11",
        LogsFolder: @"C:\Program Files\Ripcord\logs",
        CheckBitLocker: true);

    private static readonly ObservedDeployment ListenerInPlace = new(
        ServiceInstalled: true,
        ServiceBinaryPath: Binary,
        FirewallRuleInstalled: true,
        FirewallPort: 7443,
        FirewallRemoteAddress: "192.0.2.11",
        SnapshotReadableByService: true,
        ServiceRunning: true,
        LogsWritableByService: true,
        EventSourceRegistered: true,
        ConfigurationReadableByService: true,
        ListenerRecovers: true)
    {
        Publisher = ObservedPublisher.Absent("Hyper-V Administrators"),
    };

    private static IReadOnlyList<DeploymentAction> Actions(DeploymentPlan plan) =>
        [.. plan.Steps.Where(step => step.Service == RipcordService.Publisher).Select(step => step.Action)];

    /// Created before any grant — its account only exists once the service does — and started
    /// after the listener, so a publisher that fails still leaves something served.
    [Fact]
    public void On_a_fresh_host_the_publisher_is_created_granted_and_started_in_order()
    {
        DeploymentPlan plan = DeploymentPlan.For(Desired, DeploymentPlanTests.FreshHost);

        Assert.Equal(
            [DeploymentAction.CreateService, DeploymentAction.ConfigureRecovery,
             DeploymentAction.GrantConfigurationAccess,
             DeploymentAction.GrantSnapshotWriteAccess, DeploymentAction.GrantLogsAccess,
             DeploymentAction.AddToHyperVAdministrators, DeploymentAction.GrantEncryptionNamespaceAccess,
             DeploymentAction.StartService],
            Actions(plan));

        List<DeploymentStep> steps = [.. plan.Steps];
        Assert.True(
            steps.FindIndex(step => step.Service == RipcordService.Publisher && step.Action == DeploymentAction.CreateService)
            < steps.FindIndex(step => step.Action == DeploymentAction.CreateFirewallRule));
        Assert.True(
            steps.FindIndex(step => step.Service == RipcordService.Listener && step.Action == DeploymentAction.StartService)
            < steps.FindIndex(step => step.Service == RipcordService.Publisher && step.Action == DeploymentAction.StartService));
    }

    [Fact]
    public void Everything_in_place_is_nothing_to_do() =>
        Assert.Empty(DeploymentPlan.For(
            Desired,
            ListenerInPlace with { Publisher = DeploymentPlanTests.PublisherInPlace(Binary, NamespaceGrant.Granted) }).Steps);

    /// Membership reaches a process only at its next start.
    [Fact]
    public void A_running_publisher_just_added_to_the_group_is_restarted()
    {
        DeploymentPlan plan = DeploymentPlan.For(
            Desired,
            ListenerInPlace with
            {
                Publisher = DeploymentPlanTests.PublisherInPlace(Binary, NamespaceGrant.Granted) with { InHyperVAdministrators = false },
            });

        Assert.Equal([DeploymentAction.AddToHyperVAdministrators, DeploymentAction.RestartService], Actions(plan));
        Assert.Equal("Hyper-V Administrators", plan.Steps[0].Target);
    }

    /// The listener faces the network: it must not be able to rewrite the publisher's log.
    [Fact]
    public void A_logs_folder_the_listener_can_write_is_taken_out_of_its_reach()
    {
        DeploymentStep step = Assert.Single(DeploymentPlan.For(
            Desired,
            ListenerInPlace with
            {
                Publisher = DeploymentPlanTests.PublisherInPlace(Binary, NamespaceGrant.Granted) with { ListenerCanWriteItsLogs = true },
            }).Steps);

        Assert.Equal(DeploymentAction.GrantLogsAccess, step.Action);
        Assert.Contains(@"logs\publish", step.Description, StringComparison.Ordinal);
        Assert.Contains(@"keep NT SERVICE\ripcord out", step.Description, StringComparison.Ordinal);
    }

    /// BitLocker only while the configuration checks it; switched off, the access goes.
    [Fact]
    public void The_bitlocker_ace_follows_the_configuration()
    {
        ObservedDeployment granted = ListenerInPlace with
        {
            Publisher = DeploymentPlanTests.PublisherInPlace(Binary, NamespaceGrant.Granted),
        };

        Assert.Equal(
            [DeploymentAction.RevokeEncryptionNamespaceAccess],
            Actions(DeploymentPlan.For(Desired with { CheckBitLocker = false }, granted)));
        Assert.Empty(Actions(DeploymentPlan.For(
            Desired with { CheckBitLocker = false },
            ListenerInPlace with { Publisher = DeploymentPlanTests.PublisherInPlace(Binary, NamespaceGrant.Missing) })));
        Assert.Empty(Actions(DeploymentPlan.For(
            Desired,
            ListenerInPlace with { Publisher = DeploymentPlanTests.PublisherInPlace(Binary, NamespaceGrant.Unmodifiable) })));
    }

    [Fact]
    public void A_host_with_no_hyper_v_administrators_group_is_refused_before_anything()
    {
        DeploymentPlan plan = DeploymentPlan.For(
            Desired, ObservedDeployment.Nothing with { Publisher = ObservedPublisher.Absent(null) });

        Assert.True(plan.IsBlocked);
        Assert.Empty(plan.Steps);
        Assert.Contains("S-1-5-32-578", plan.BlockedBy, StringComparison.Ordinal);
    }

    /// Stopped first, its access taken back while its account still resolves, deleted last —
    /// and all of it before the listener's own removal.
    [Fact]
    public void Removal_takes_everything_back_before_the_service_goes()
    {
        DeploymentPlan plan = DeploymentPlan.ToRemove(ListenerInPlace with
        {
            Publisher = DeploymentPlanTests.PublisherInPlace(Binary, NamespaceGrant.Granted),
        });

        Assert.Equal(
            [DeploymentAction.StopService, DeploymentAction.RevokeEncryptionNamespaceAccess,
             DeploymentAction.RemoveFromHyperVAdministrators, DeploymentAction.RevokeLogsAccess,
             DeploymentAction.RevokeSnapshotAccess, DeploymentAction.RevokeConfigurationAccess,
             DeploymentAction.RemoveService],
            Actions(plan));
        Assert.Equal(RipcordService.Publisher, plan.Steps[0].Service);
        Assert.True(plan.Steps.TakeWhile(step => step.Service == RipcordService.Publisher).Count() == 7);
    }

    /// Membership that could not be read is tried: once the service is deleted, a member left
    /// behind stays in the group as a SID nothing resolves.
    [Fact]
    public void Removal_leaves_the_group_even_when_membership_could_not_be_read() =>
        Assert.Contains(
            DeploymentAction.RemoveFromHyperVAdministrators,
            Actions(DeploymentPlan.ToRemove(ListenerInPlace with
            {
                Publisher = DeploymentPlanTests.PublisherInPlace(Binary) with { InHyperVAdministrators = null },
            })));

    /// The ACE names a computed SID, so it can still be taken back once the service is gone.
    [Fact]
    public void An_ace_outliving_its_service_is_still_taken_back() =>
        Assert.Equal(
            [DeploymentAction.RevokeEncryptionNamespaceAccess],
            Actions(DeploymentPlan.ToRemove(ListenerInPlace with
            {
                Publisher = ObservedPublisher.Absent("Hyper-V Administrators") with { Encryption = NamespaceGrant.Granted },
            })));

    /// The dry-run shows the exact ACE, SID included.
    [Fact]
    public void The_bitlocker_step_names_the_exact_ace() =>
        Assert.Contains(
            NamespaceAcl.Ace(VirtualAccount.Sid("ripcord-publish")),
            DeploymentPlan.For(Desired, DeploymentPlanTests.FreshHost).Steps
                .Single(step => step.Action == DeploymentAction.GrantEncryptionNamespaceAccess).Description,
            StringComparison.Ordinal);

    [Fact]
    public void A_publisher_that_is_not_installed_has_nothing_to_take_back() =>
        Assert.Empty(Actions(DeploymentPlan.ToRemove(ListenerInPlace)));
}
