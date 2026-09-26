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

    /// A virtual account cannot read a machine key until it is granted: the handshake then
    /// fails on this host's own certificate.
    [Fact]
    public void An_unreadable_private_key_is_granted_before_the_service_starts()
    {
        DeploymentPlan plan = DeploymentPlan.For(
            Desired with { CertificateThumbprint = "AB12" },
            ObservedDeployment.Nothing with { KeyReadableByService = false });

        List<DeploymentAction> actions = [.. plan.Steps.Select(step => step.Action)];

        Assert.Contains(DeploymentAction.GrantKeyAccess, actions);
        Assert.True(
            actions.IndexOf(DeploymentAction.GrantKeyAccess)
                < actions.IndexOf(DeploymentAction.StartService));
        Assert.Contains("AB12", plan.Steps.Single(step => step.Action == DeploymentAction.GrantKeyAccess).Description, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void A_readable_or_unfound_key_needs_no_grant(bool? readable) =>
        Assert.DoesNotContain(
            DeploymentPlan.For(
                    Desired with { CertificateThumbprint = "AB12" },
                    Matching() with { KeyReadableByService = readable })
                .Steps,
            step => step.Action == DeploymentAction.GrantKeyAccess);

    [Fact]
    public void Removal_revokes_the_key_access_it_granted() =>
        Assert.Contains(
            DeploymentAction.RevokeKeyAccess,
            DeploymentPlan.ToRemove(Matching() with { KeyReadableByService = true })
                .Steps.Select(step => step.Action));

    [Fact]
    public void A_disabled_listener_is_blocked_before_any_step()
    {
        DeploymentPlan plan = DeploymentPlan.For(
            Desired with { ListenerEnabled = false }, ObservedDeployment.Nothing);

        Assert.Empty(plan.Steps);
        Assert.Equal(DeploymentPlan.ListenerDisabled, plan.BlockedBy);
    }

    [Fact]
    public void On_a_fresh_host_everything_is_created()
    {
        DeploymentPlan plan = DeploymentPlan.For(Desired, ObservedDeployment.Nothing);

        Assert.Equal(
            [DeploymentAction.CreateService, DeploymentAction.ConfigureRecovery,
             DeploymentAction.CreateFirewallRule,
             DeploymentAction.GrantConfigurationAccess,
             DeploymentAction.GrantSnapshotAccess, DeploymentAction.GrantLogsAccess,
             DeploymentAction.RegisterEventSource, DeploymentAction.StartService],
            Listener(plan));
        Assert.True(plan.ChangesAnything);
    }

    /// A crash restarts either service a minute later; a service that stops itself with an
    /// exit code is left alone, or it would refuse a bad configuration once a minute for ever.
    [Fact]
    public void A_service_without_recovery_gets_it_and_one_with_it_does_not()
    {
        DeploymentStep step = Assert.Single(DeploymentPlan.For(
            Desired, Matching() with { ListenerRecovers = false }).Steps);

        Assert.Equal(DeploymentAction.ConfigureRecovery, step.Action);
        Assert.Null(step.Service);
        Assert.Contains("actions= restart/60000", step.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("failureflag", step.Description, StringComparison.Ordinal);
    }

    /// An upgraded host: 0.7.0's broad read is narrowed to the configuration grant, after the
    /// restart that moves the listener off the install folder, never before.
    [Fact]
    public void The_old_broad_install_folder_read_is_narrowed_last()
    {
        DeploymentPlan plan = DeploymentPlan.For(
            Desired, Matching() with { InstallFolderGrantBroad = true, ListenerOutdated = true });

        Assert.Equal(
            [DeploymentAction.RestartService, DeploymentAction.NarrowConfigurationAccess],
            Listener(plan));
    }

    /// The registry's SERVICE_FAILURE_ACTIONS: reset, two pointers, count, pointer, then pairs.
    [Fact]
    public void Recovery_is_read_from_the_registry_blob()
    {
        byte[] restart = [.. BitConverter.GetBytes(86_400), .. new byte[8], .. BitConverter.GetBytes(1),
            .. new byte[4], .. BitConverter.GetBytes(1), .. BitConverter.GetBytes(60_000)];
        byte[] nothing = [.. restart[..12], .. BitConverter.GetBytes(0), .. restart[16..]];
        byte[] reboot = [.. restart[..20], .. BitConverter.GetBytes(2), .. restart[24..]];

        Assert.True(ServiceRecovery.Restarts(restart));
        Assert.False(ServiceRecovery.Restarts(nothing));
        Assert.False(ServiceRecovery.Restarts(reboot));
        Assert.False(ServiceRecovery.Restarts(null));
        Assert.False(ServiceRecovery.Restarts(restart[..20]));
    }

    /// The listener reads ripcord.yaml at every start; the grant is read-only, on the files of
    /// the install folder, and taken back on removal only where it is Ripcord's own entry.
    [Fact]
    public void Reading_the_configuration_is_granted_and_taken_back()
    {
        DeploymentStep grant = Assert.Single(
            DeploymentPlan.For(Desired, Matching() with { ConfigurationReadableByService = false }).Steps);

        Assert.Equal(DeploymentAction.GrantConfigurationAccess, grant.Action);
        Assert.Contains(@"D:\Ripcord", grant.Description, StringComparison.Ordinal);

        Assert.DoesNotContain(
            DeploymentPlan.ToRemove(ObservedDeployment.Nothing with { ConfigurationReadableByService = true }).Steps,
            step => step.Action == DeploymentAction.RevokeConfigurationAccess);
        Assert.Contains(
            DeploymentPlan.ToRemove(ObservedDeployment.Nothing with { InstallFolderGrantedToService = true }).Steps,
            step => step.Action == DeploymentAction.RevokeConfigurationAccess);
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
            Listener(plan));
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
        List<DeploymentAction> install = [.. Listener(DeploymentPlan.For(Desired, ObservedDeployment.Nothing))];

        List<DeploymentAction> remove = [.. Listener(DeploymentPlan.ToRemove(Matching()))];

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
            Listener(plan).Where(action =>
                action is DeploymentAction.CreateService or DeploymentAction.StartService));

        // And the start is last of the listener's: after the firewall rule and after the access
        // it needs to the file it serves.
        Assert.Equal(DeploymentAction.StartService, Listener(plan)[^1]);
    }

    private const string CurrentKey = @"C:\ProgramData\Microsoft\Crypto\Keys\current_key";

    private const string OldKey = @"C:\ProgramData\Microsoft\Crypto\Keys\old_key";

    /// After `ripcord pair` or a renewal the old key stays readable by the service account;
    /// the next install takes that back, last, after everything the listener needs.
    [Fact]
    public void A_key_no_configured_certificate_uses_is_revoked_last()
    {
        DeploymentPlan plan = DeploymentPlan.For(
            Desired,
            Matching() with
            {
                ServiceRunning = false,
                KeyFile = CurrentKey,
                KeyFilesGranted = [CurrentKey.ToUpperInvariant(), OldKey],
            });

        DeploymentStep revoke = plan.Steps[^1];
        Assert.Equal(DeploymentAction.RevokeStaleKeyAccess, revoke.Action);
        Assert.Equal(OldKey, revoke.Target);
        Assert.False(revoke.Recursive);
        Assert.Equal(DeploymentAction.StartService, plan.Steps[^2].Action);
        Assert.Single(plan.Steps, step => step.Action == DeploymentAction.RevokeStaleKeyAccess);
    }

    /// Which key is current cannot be told without it: none is taken.
    [Fact]
    public void With_the_current_key_not_found_no_key_is_revoked() =>
        Assert.Empty(DeploymentPlan.For(
            Desired, Matching() with { KeyFile = null, KeyFilesGranted = [OldKey] }).Steps);

    /// The service moved: the old folder and its logs lose their grants. Recursively only where
    /// nothing in use lies below.
    [Fact]
    public void The_old_install_folder_is_a_candidate_once_the_service_moved()
    {
        Assert.Equal(
            [@"C:\Old", @"C:\Old\logs"],
            DeploymentPlan.StaleFolderCandidates(Desired, @"C:\Old\ripcord.exe"));
        Assert.Empty(DeploymentPlan.StaleFolderCandidates(Desired, Desired.BinaryPath));
        Assert.Empty(DeploymentPlan.StaleFolderCandidates(
            Desired with { LogsFolder = @"C:\Old\logs", SnapshotPath = @"C:\Old\state.json" },
            @"C:\Old\ripcord.exe"));
    }

    [Fact]
    public void A_stale_folder_holding_what_is_in_use_is_not_revoked_recursively()
    {
        DesiredDeployment desired = Desired with { LogsFolder = @"C:\Old\keep\logs" };

        IReadOnlyList<DeploymentStep> revokes = [.. DeploymentPlan.For(
                desired, Matching() with { FoldersGranted = [@"C:\Old", @"C:\Other"] })
            .Steps.Where(step => step.Action == DeploymentAction.RevokeStaleFolderAccess)];

        Assert.Equal(2, revokes.Count);
        Assert.False(revokes.Single(step => step.Target == @"C:\Old").Recursive);
        Assert.True(revokes.Single(step => step.Target == @"C:\Other").Recursive);
    }

    /// Moved into a folder below the old one: `/t` would reach the new install folder.
    [Fact]
    public void A_stale_folder_holding_the_install_folder_is_not_revoked_recursively()
    {
        DeploymentStep revoke = DeploymentPlan.For(
                Desired, Matching() with { FoldersGranted = [@"D:\"] })
            .Steps.Single(step => step.Action == DeploymentAction.RevokeStaleFolderAccess);

        Assert.False(revoke.Recursive);
    }

    /// A real key path is over 100 characters. The step names the file alone, which fits the
    /// 70 columns after the step number, and keeps the full path as its target.
    [Fact]
    public void A_stale_key_step_names_the_file_on_a_line_of_its_own()
    {
        const string Key = @"C:\ProgramData\Microsoft\Crypto\RSA\MachineKeys\"
            + "0123456789abcdef0123456789abcdef_01234567-89ab-cdef-0123-456789abcdef";

        DeploymentStep revoke = DeploymentPlan.For(
                Desired, Matching() with { KeyFile = CurrentKey, KeyFilesGranted = [Key] })
            .Steps.Single(step => step.Action == DeploymentAction.RevokeStaleKeyAccess);

        Assert.Equal(Key, revoke.Target);
        Assert.All(revoke.Description.Split(' '), word => Assert.True(word.Length <= 70));
        Assert.EndsWith(Key[(Key.LastIndexOf('\\') + 1)..], revoke.Description, StringComparison.Ordinal);
        Assert.Contains(@"C:\ProgramData\Microsoft\Crypto\RSA\MachineKeys,", revoke.Reason, StringComparison.Ordinal);
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
        EventSourceRegistered: true,
        ConfigurationReadableByService: true,
        Publisher: PublisherInPlace(@"D:\Ripcord\ripcord.exe"),
        ListenerRecovers: true);

    private static List<DeploymentAction> Listener(DeploymentPlan plan) =>
        [.. plan.Steps.Where(step => step.Service is null).Select(step => step.Action)];

    /// A publisher that needs nothing: running this binary, granted everything, in the group.
    internal static ObservedPublisher PublisherInPlace(string binaryPath, NamespaceGrant encryption = NamespaceGrant.Missing) =>
        new(
            new ObservedService(true, $"\"{binaryPath}\" publish", ServiceRunState.Running, "Auto", 0, 0),
            ConfigurationReadable: true,
            SnapshotWritable: true,
            LogsWritable: true,
            ListenerCanWriteItsLogs: false,
            InstallFolderGranted: true,
            HyperVAdministrators: "Hyper-V Administrators",
            InHyperVAdministrators: true,
            Encryption: encryption,
            EncryptionNote: null,
            Recovers: true);

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

    [Fact]
    public void The_grant_step_says_what_the_snapshot_is()
    {
        DeploymentStep grant = DeploymentPlan.For(Desired, ObservedDeployment.Nothing).Steps
            .Single(step => step.Action == DeploymentAction.GrantSnapshotAccess);

        Assert.Contains("state.json", grant.Reason, StringComparison.Ordinal);
        Assert.Contains("the publishing service writes", grant.Reason, StringComparison.Ordinal);
        Assert.Contains("serves to the peer", grant.Reason, StringComparison.Ordinal);
    }
}
