using Ripcord.Domain.Failover;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Failover;

/// What to do to the original primary the moment it comes back after an unplanned failover.
///
/// Both hosts sit on the same external switch on the same subnet. With the common
/// `StartIfRunning` default, restoring power boots the *original* domain controller alongside
/// the failed-over copy — two domain controllers, same identity, same IP, both writing. That
/// is the state no sequence in this tool repairs, and `RecoveryHistory 0` leaves no earlier
/// point to fall back to. Fencing is what stops it arising.
public class FencingTests
{
    private static readonly IReadOnlyList<string> FailedOver = ["VM-DC-01", "VM-LEGACY-01"];

    [Fact]
    public void A_vm_that_would_boot_itself_is_fenced_and_its_previous_setting_recorded()
    {
        FencePlan plan = Fencing.Plan(
            [Vm("VM-DC-01", AutomaticStartAction.StartIfRunning, VmPowerState.Off)],
            ["VM-DC-01"]);

        FenceAction action = Assert.Single(plan.ToFence);
        Assert.Equal("VM-DC-01", action.VmName);
        Assert.Equal(AutomaticStartAction.StartIfRunning, action.Previous);
        Assert.True(action.PreviousIsKnown);
        Assert.Null(plan.Halt);
    }

    [Fact]
    public void A_vm_already_set_to_nothing_is_left_alone()
    {
        FencePlan plan = Fencing.Plan(
            [Vm("VM-DC-01", AutomaticStartAction.Nothing, VmPowerState.Off)],
            ["VM-DC-01"]);

        Assert.Empty(plan.ToFence);
        Assert.Equal(["VM-DC-01"], plan.AlreadyFenced);
    }

    /// The trade-off, stated rather than hidden: an unread setting is still fenced, because a
    /// host that boots the old domain controller is worse than a restore value nobody has.
    /// What is lost is `reprotect`'s ability to put the setting back, so the run says so.
    [Fact]
    public void An_unreadable_setting_is_fenced_anyway_and_flagged_as_unrestorable()
    {
        FencePlan plan = Fencing.Plan(
            [Vm("VM-DC-01", null, VmPowerState.Off)], ["VM-DC-01"]);

        FenceAction action = Assert.Single(plan.ToFence);
        Assert.False(action.PreviousIsKnown);
        Assert.Equal(AutomaticStartAction.Unknown, action.Previous);
    }

    /// Fencing prevents a future boot. A VM already running here is not a future boot — it is
    /// the divergence, under way. Setting a start action would leave it running and report
    /// success.
    [Fact]
    public void A_vm_already_running_halts_the_fence_rather_than_being_fenced()
    {
        FencePlan plan = Fencing.Plan(
            [
                Vm("VM-DC-01", AutomaticStartAction.StartIfRunning, VmPowerState.Running),
                Vm("VM-LEGACY-01", AutomaticStartAction.StartIfRunning, VmPowerState.Off),
            ],
            FailedOver);

        Assert.NotNull(plan.Halt);
        Assert.Contains("VM-DC-01", plan.Halt);
        Assert.Empty(plan.ToFence);
    }

    [Fact]
    public void A_vm_that_is_starting_halts_it_too()
    {
        FencePlan plan = Fencing.Plan(
            [Vm("VM-DC-01", AutomaticStartAction.Start, VmPowerState.Starting)], ["VM-DC-01"]);

        Assert.NotNull(plan.Halt);
    }

    /// Saved and paused are not running, and they are not off either: both can be resumed into
    /// a second live claimant. Fenced, and named, so nobody reads "fenced" as "safe".
    [Theory]
    [InlineData(VmPowerState.Saved)]
    [InlineData(VmPowerState.Paused)]
    [InlineData(VmPowerState.Unknown)]
    public void A_vm_that_is_not_confirmed_off_is_fenced_and_named(VmPowerState power)
    {
        FencePlan plan = Fencing.Plan(
            [Vm("VM-DC-01", AutomaticStartAction.Start, power)], ["VM-DC-01"]);

        Assert.Single(plan.ToFence);
        Assert.Equal(["VM-DC-01"], plan.NotConfirmedOff);
        Assert.Null(plan.Halt);
    }

    [Fact]
    public void A_vm_whose_power_was_not_read_is_not_confirmed_off()
    {
        FencePlan plan = Fencing.Plan(
            [Vm("VM-DC-01", AutomaticStartAction.Start, power: null)], ["VM-DC-01"]);

        Assert.Equal(["VM-DC-01"], plan.NotConfirmedOff);
    }

    /// A failed-over VM the returning host does not have is not a danger, but it is a surprise,
    /// and a surprise about which host holds what is worth printing.
    [Fact]
    public void A_failed_over_vm_absent_from_this_host_is_reported_not_invented()
    {
        FencePlan plan = Fencing.Plan(
            [Vm("VM-DC-01", AutomaticStartAction.Nothing, VmPowerState.Off)], FailedOver);

        Assert.Equal(["VM-LEGACY-01"], plan.Absent);
    }

    /// The host has other VMs on it. Fencing touches only what was failed over: switching off
    /// the auto-start of a machine nobody moved is a change made to production for no reason.
    [Fact]
    public void A_vm_that_was_not_failed_over_is_left_entirely_alone()
    {
        FencePlan plan = Fencing.Plan(
            [
                Vm("VM-DC-01", AutomaticStartAction.StartIfRunning, VmPowerState.Off),
                Vm("VM-OTHER-01", AutomaticStartAction.StartIfRunning, VmPowerState.Running),
            ],
            ["VM-DC-01"]);

        Assert.Equal("VM-DC-01", Assert.Single(plan.ToFence).VmName);
        Assert.Null(plan.Halt);
    }

    [Fact]
    public void Vm_names_are_matched_case_insensitively()
    {
        FencePlan plan = Fencing.Plan(
            [Vm("vm-dc-01", AutomaticStartAction.Start, VmPowerState.Off)], ["VM-DC-01"]);

        Assert.Single(plan.ToFence);
        Assert.Empty(plan.Absent);
    }

    [Fact]
    public void A_fence_with_nothing_to_do_is_not_a_failure()
    {
        FencePlan plan = Fencing.Plan(
            [Vm("VM-DC-01", AutomaticStartAction.Nothing, VmPowerState.Off)], ["VM-DC-01"]);

        Assert.False(plan.HasWork);
        Assert.Null(plan.Halt);
    }

    private static VmReplicationState Vm(
        string name, AutomaticStartAction? startAction, VmPowerState? power) =>
        new(
            name,
            ReplicationRole.Primary,
            ReplicationState.Recovered,
            ReplicationHealth.Critical,
            null,
            null,
            null,
            power,
            startAction);

    /// Which VMs the fence is about, read off the host that took them. A copy serving after a
    /// failover, or simply running where a replica should be idle, is one that moved.
    [Fact]
    public void The_failed_over_set_is_read_from_the_host_that_took_them()
    {
        IReadOnlyList<VmReplicationState> onPeer =
        [
            Serving("VM-DC-01"),
            new VmReplicationState(
                "VM-LEGACY-01",
                ReplicationRole.Replica,
                ReplicationState.Replicating,
                ReplicationHealth.Normal,
                null,
                null,
                null,
                VmPowerState.Off),
        ];

        Assert.Equal(["VM-DC-01"], Fencing.FailedOver(onPeer));
    }

    /// A replica that is simply running is a claimant whatever its replication state says.
    [Fact]
    public void A_running_copy_counts_as_failed_over_even_without_a_recovered_state()
    {
        IReadOnlyList<VmReplicationState> onPeer =
        [
            new VmReplicationState(
                "VM-DC-01",
                ReplicationRole.Replica,
                ReplicationState.Replicating,
                ReplicationHealth.Normal,
                null,
                null,
                null,
                VmPowerState.Running),
        ];

        Assert.Equal(["VM-DC-01"], Fencing.FailedOver(onPeer));
    }

    private static VmReplicationState Serving(string name) =>
        new(
            name,
            ReplicationRole.Replica,
            ReplicationState.Recovered,
            ReplicationHealth.Critical,
            null,
            null,
            null,
            VmPowerState.Off);
}
