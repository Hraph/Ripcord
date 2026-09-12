using Ripcord.Domain.Inventory;
using Ripcord.Domain.TestFailover;

namespace Ripcord.Tests.TestFailover;

/// The rule that stands between a monthly test and a second live domain controller on the
/// production LAN. A test VM is built from the replica's configuration, and a milestone 2
/// critical rule requires that configuration to point at the production switch — so started
/// as-is, a test failover puts a duplicate identity on the real network.
///
/// Every case here answers one question: could this adapter carry traffic to production?
/// Anything that cannot be answered is refused, never assumed safe.
public class IsolationTests
{
    private const string TestSwitch = "vSwitch-ISOLATED";
    private const string ProductionSwitch = "vSwitch-PROD";

    [Fact]
    public void An_adapter_bound_to_no_switch_is_isolated()
    {
        Assert.True(Assess(Adapter(switchName: null, connected: null)).IsIsolated);
    }

    [Fact]
    public void An_adapter_on_the_test_switch_is_isolated()
    {
        Assert.True(Assess(Adapter(TestSwitch, connected: true)).IsIsolated);
    }

    /// Hyper-V preserves the case a switch was created with, and the configuration is typed
    /// by hand. A case difference is not a different switch.
    [Fact]
    public void The_test_switch_is_matched_case_insensitively()
    {
        Assert.True(Assess(Adapter("vswitch-isolated", connected: true)).IsIsolated);
    }

    /// The documented alternative to the test switch: the adapter is on production but
    /// explicitly unplugged, so it carries nothing.
    [Fact]
    public void An_adapter_explicitly_disconnected_is_isolated_whatever_switch_it_names()
    {
        Assert.True(Assess(Adapter(ProductionSwitch, connected: false)).IsIsolated);
    }

    [Fact]
    public void An_adapter_connected_to_another_switch_breaches_isolation()
    {
        IsolationAssessment assessment = Assess(Adapter(ProductionSwitch, connected: true));

        IsolationBreach breach = Assert.Single(assessment.Breaches);

        Assert.False(assessment.IsIsolated);
        Assert.Equal(IsolationDoubt.Connected, breach.Doubt);
        Assert.Contains(ProductionSwitch, breach.Observed);
    }

    /// V6: which CIM class reports "disconnected" as against "bound to no switch" is not
    /// settled, so the connection flag can come back unread. An unread flag on a named
    /// switch is the case that must never be taken for disconnected.
    [Fact]
    public void An_adapter_whose_connection_cannot_be_read_breaches_isolation()
    {
        IsolationAssessment assessment = Assess(Adapter(ProductionSwitch, connected: null));

        IsolationBreach breach = Assert.Single(assessment.Breaches);

        Assert.False(assessment.IsIsolated);
        Assert.Equal(IsolationDoubt.Unreadable, breach.Doubt);
    }

    /// The two breach kinds are rendered differently and must never be conflated: one is
    /// "this is wired to production", the other is "nobody knows what this is wired to".
    [Fact]
    public void An_unreadable_adapter_does_not_claim_to_be_connected()
    {
        IsolationBreach breach = Assert.Single(
            Assess(Adapter(ProductionSwitch, connected: null)).Breaches);

        Assert.DoesNotContain("connected", breach.Observed, StringComparison.OrdinalIgnoreCase);
    }

    /// With no test switch configured the only isolated state is disconnected. A named,
    /// connected switch cannot accidentally satisfy a rule that was never configured.
    [Fact]
    public void With_no_test_switch_configured_a_connected_adapter_breaches_isolation()
    {
        Assert.False(
            Assess(Adapter(TestSwitch, connected: true), testSwitch: null).IsIsolated);
    }

    /// Every failing adapter is named. A VM with two production NICs that reported only the
    /// first would be fixed once and refused again.
    [Fact]
    public void Every_breaching_adapter_is_named()
    {
        IsolationAssessment assessment = Assess(
            Adapter(ProductionSwitch, connected: true, name: "Network Adapter"),
            Adapter("vSwitch-DMZ", connected: true, name: "Network Adapter 2"));

        Assert.Equal(2, assessment.Breaches.Count);
        Assert.Contains(assessment.Breaches, breach => breach.Adapter == "Network Adapter");
        Assert.Contains(assessment.Breaches, breach => breach.Adapter == "Network Adapter 2");
    }

    [Fact]
    public void A_vm_with_no_adapters_is_isolated()
    {
        Assert.True(Assess().IsIsolated);
    }

    /// The adapters could not be read at all, which is not the same as a VM having none.
    /// Milestone 3 refuses on it; the empty case above is genuinely isolated.
    [Fact]
    public void Adapters_that_could_not_be_read_are_refused()
    {
        IsolationAssessment assessment = Isolation.Of(null, TestSwitch);

        IsolationBreach breach = Assert.Single(assessment.Breaches);

        Assert.False(assessment.IsIsolated);
        Assert.Equal(IsolationDoubt.Unreadable, breach.Doubt);
    }

    private static IsolationAssessment Assess(params VirtualAdapter[] adapters) =>
        Isolation.Of(adapters, TestSwitch);

    private static IsolationAssessment Assess(VirtualAdapter adapter, string? testSwitch) =>
        Isolation.Of([adapter], testSwitch);

    private static VirtualAdapter Adapter(
        string? switchName, bool? connected, string name = "Network Adapter") =>
        new(name, switchName, connected, "00-15-5D-01-02-01", false, null);
}
