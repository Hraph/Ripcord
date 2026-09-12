using Ripcord.Domain.Checks;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Checks;

/// The four network rules, each in its three states: violated, satisfied, and unanswerable.
/// They are what stands between "the VM started" and "the VM is reachable" — the host-level
/// switch check passes in every one of these cases.
public class NetworkRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_replica_on_the_wrong_switch_is_critical()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { SwitchName = "vSwitch-OLD" }))
            .For(CheckRules.ReplicaSwitchMismatch));

        Assert.Equal(Severity.Critical, finding.Rule.Severity);
        Assert.Equal("VM-DC-01", finding.Subject);
        Assert.Contains("vSwitch-OLD", finding.Observed);
        Assert.Contains("vSwitch-PROD", finding.Remedy);
    }

    /// The switch name is compared case-insensitively: Hyper-V preserves whatever case the
    /// switch was created with, and a case difference is not a misconfiguration.
    [Fact]
    public void A_switch_name_differing_only_in_case_is_the_same_switch()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { SwitchName = "vswitch-prod" }))
            .For(CheckRules.ReplicaSwitchMismatch));
    }

    /// Bound to no switch at all, which is a different fault from being on the wrong one:
    /// no network whatsoever rather than the wrong network.
    [Fact]
    public void An_adapter_bound_to_no_switch_is_critical()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { SwitchName = null }))
            .For(CheckRules.ReplicaAdapterDisconnected));

        Assert.Equal(Severity.Critical, finding.Rule.Severity);
        Assert.Contains("no network at all", finding.Implication);
    }

    [Fact]
    public void An_adapter_reported_disconnected_is_critical()
    {
        Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { IsConnected = false }))
            .For(CheckRules.ReplicaAdapterDisconnected));
    }

    /// V6 leaves it unsettled which CIM class reports the binding. An unread binding beside a
    /// switch name is taken as attached: the switch name is the stronger signal, and treating
    /// it as disconnected would fire on every host where that property is absent.
    [Fact]
    public void An_unread_connection_flag_beside_a_switch_name_is_not_a_fault()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { IsConnected = null }))
            .For(CheckRules.ReplicaAdapterDisconnected));
    }

    /// A dynamic MAC is the quietest of these faults: the VM boots, the switch is right, and
    /// the guest has lost its whole network identity.
    [Fact]
    public void A_dynamic_mac_on_the_replica_is_critical()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { UsesDynamicMac = true }))
            .For(CheckRules.ReplicaMacDrift));

        Assert.Equal(Severity.Critical, finding.Rule.Severity);
        Assert.Contains("unconfigured NIC", finding.Implication);
    }

    [Fact]
    public void A_mac_differing_from_the_primarys_is_critical()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { MacAddress = "00-15-5D-09-09-09" }))
            .For(CheckRules.ReplicaMacDrift));

        Assert.Contains("00-15-5D-09-09-09", finding.Observed);
        Assert.Contains("00-15-5D-01-02-01", finding.Remedy);
    }

    /// The same address written two ways is the same address.
    [Fact]
    public void A_mac_differing_only_in_separators_is_the_same_address()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { MacAddress = "00155d010201" }))
            .For(CheckRules.ReplicaMacDrift));
    }

    /// All zeroes is what a dynamic MAC reports before the VM has ever started. It is not an
    /// address, and two VMs both reporting it must not compare equal.
    [Fact]
    public void An_unassigned_mac_cannot_be_compared()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { MacAddress = "00-00-00-00-00-00" }));

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.ReplicaMacDrift)).Verdict);
    }

    [Fact]
    public void A_vlan_differing_between_the_two_sides_is_critical()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { VlanId = 20 }))
            .For(CheckRules.VlanMismatch));

        Assert.Equal(Severity.Critical, finding.Rule.Severity);
        Assert.Contains("VLAN 20", finding.Observed);
        Assert.Contains("VLAN 10", finding.Observed);
        Assert.Contains("broadcast domain", finding.Implication);
    }

    /// Untagged on the target and tagged on the source is a mismatch, not a pair of unknowns.
    [Fact]
    public void An_untagged_replica_facing_a_tagged_primary_is_critical()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { VlanId = null }))
            .For(CheckRules.VlanMismatch));

        Assert.Contains("no VLAN tag", finding.Observed);
    }

    [Fact]
    public void Untagged_on_both_sides_is_not_a_mismatch()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now)
                .WithTargetAdapter("VM-DC-01", adapter => adapter with { VlanId = null })
                .WithSourceAdapter("VM-DC-01", adapter => adapter with { VlanId = null }))
            .For(CheckRules.VlanMismatch));
    }

    /// A target copy nobody could read blocks all four answers, and says so four times: one
    /// line saying "the network could not be checked" would hide which answer is missing.
    [Fact]
    public void An_unreadable_target_copy_leaves_all_four_rules_unevaluable()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithTargetVm("VM-DC-01", vm => vm with { Facts = null }));

        string[] unevaluated =
        [
            .. report.Unevaluated
                .Where(finding => finding.Subject == "VM-DC-01")
                .Select(finding => finding.Rule.Id),
        ];

        Assert.Contains(CheckRules.ReplicaSwitchMismatch, unevaluated);
        Assert.Contains(CheckRules.ReplicaAdapterDisconnected, unevaluated);
        Assert.Contains(CheckRules.ReplicaMacDrift, unevaluated);
        Assert.Contains(CheckRules.VlanMismatch, unevaluated);
    }

    /// The MAC and the VLAN are comparisons; with no primary copy to compare against they are
    /// unanswerable, while the switch name is still judged against the configuration.
    [Fact]
    public void With_no_primary_copy_the_comparisons_are_unevaluable_and_the_switch_is_not()
    {
        CheckReport report = Report(Pairs.Healthy(Now).WithoutSourceVm("VM-DC-01"));

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.ReplicaMacDrift)).Verdict);

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.VlanMismatch)).Verdict);

        Assert.Empty(report.For(CheckRules.ReplicaSwitchMismatch));
    }

    /// A VM with no adapter read at all is not a VM with a correct adapter.
    [Fact]
    public void A_vm_with_no_adapter_read_is_unevaluable_rather_than_correct()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithTarget("VM-DC-01", facts => facts with { Adapters = [] }));

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.ReplicaSwitchMismatch)).Verdict);
    }

    /// The rules judge the target's copy. Breaking the primary's switch changes nothing about
    /// where the replica would boot.
    [Fact]
    public void The_primarys_own_switch_is_not_what_these_rules_judge()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now).WithSourceAdapter(
                "VM-DC-01", adapter => adapter with { SwitchName = "vSwitch-OLD" }))
            .For(CheckRules.ReplicaSwitchMismatch));
    }

    private static CheckReport Report(PairView view) => Pairs.Evaluate(view, Now);
}
