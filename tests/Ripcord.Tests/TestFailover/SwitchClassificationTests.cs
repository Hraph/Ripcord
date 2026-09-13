using Ripcord.Domain.Inventory;

namespace Ripcord.Tests.TestFailover;

/// Deducing Private from an absence, and when that deduction may be trusted. A wrong
/// association class, wrong role names or insufficient privilege all return an empty set
/// rather than throwing — and every switch on the host would then be Private, which the
/// isolation rule treats as safe. That is the difference between failing closed and open.
public class SwitchClassificationTests
{
    [Fact]
    public void A_switch_reaching_a_physical_nic_is_external()
    {
        Assert.Equal(
            SwitchConnectivity.External,
            SwitchClassification.Of(true, false, portsWereEnumerated: true));
    }

    /// External is established positively from a binding that was seen, so it stands even
    /// when the rest of the enumeration is in doubt.
    [Fact]
    public void An_external_switch_is_external_even_if_nothing_else_was_established()
    {
        Assert.Equal(
            SwitchConnectivity.External,
            SwitchClassification.Of(true, false, portsWereEnumerated: false));
    }

    [Fact]
    public void A_switch_reaching_only_the_management_os_is_internal()
    {
        Assert.Equal(
            SwitchConnectivity.Internal,
            SwitchClassification.Of(false, true, portsWereEnumerated: true));
    }

    [Fact]
    public void A_switch_reaching_neither_is_private_once_the_traversal_is_proven()
    {
        Assert.Equal(
            SwitchConnectivity.Private,
            SwitchClassification.Of(false, false, portsWereEnumerated: true));
    }

    /// The whole reason this function exists. Absence of evidence is not evidence of absence,
    /// and reading it as Private would boot a test VM onto whatever it is really attached to.
    [Fact]
    public void An_unenumerated_switch_is_left_unclassified()
    {
        Assert.Equal(
            SwitchConnectivity.Unknown,
            SwitchClassification.Of(false, false, portsWereEnumerated: false));
    }

    /// A switch with no ports at all is unclassified rather than private: even a private
    /// switch carries the ports of the VMs on it, so an empty enumeration is a failure to
    /// look rather than a fact about the switch.
    [Fact]
    public void A_switch_whose_ports_could_not_be_enumerated_is_unclassified()
    {
        Assert.Equal(
            SwitchConnectivity.Unknown,
            SwitchClassification.Of(false, true, portsWereEnumerated: false));
    }
}
