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
            SwitchClassification.Of(true, false, externalTraversalProven: true));
    }

    /// External is established positively, so it stands even when nothing else does.
    [Fact]
    public void An_external_switch_is_external_even_if_nothing_was_proven()
    {
        Assert.Equal(
            SwitchConnectivity.External,
            SwitchClassification.Of(true, false, externalTraversalProven: false));
    }

    [Fact]
    public void A_switch_reaching_only_the_management_os_is_internal()
    {
        Assert.Equal(
            SwitchConnectivity.Internal,
            SwitchClassification.Of(false, true, externalTraversalProven: true));
    }

    [Fact]
    public void A_switch_reaching_neither_is_private_once_the_traversal_is_proven()
    {
        Assert.Equal(
            SwitchConnectivity.Private,
            SwitchClassification.Of(false, false, externalTraversalProven: true));
    }

    /// The whole reason this function exists. Absence of evidence is not evidence of absence,
    /// and reading it as Private would boot a test VM onto whatever it is really attached to.
    [Fact]
    public void An_unproven_external_traversal_leaves_a_switch_unclassified()
    {
        Assert.Equal(
            SwitchConnectivity.Unknown,
            SwitchClassification.Of(false, false, externalTraversalProven: false));
    }

    /// The asymmetric failure: the internal traversal worked, the external one did not. A
    /// genuinely external switch missed by the broken traversal must not become Private
    /// merely because the other half of the machinery was healthy.
    [Fact]
    public void A_working_internal_traversal_does_not_vouch_for_the_external_one()
    {
        Assert.Equal(
            SwitchConnectivity.Unknown,
            SwitchClassification.Of(false, false, externalTraversalProven: false));

        Assert.Equal(
            SwitchConnectivity.Internal,
            SwitchClassification.Of(false, true, externalTraversalProven: true));
    }
}
