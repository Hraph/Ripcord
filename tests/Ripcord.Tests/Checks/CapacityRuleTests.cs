using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Checks;

/// Memory. The target has 12 GB with 4 GB reserved for the management OS, so 8 GB usable,
/// against three VMs of 2 GB. Every figure is read from the target's own copy of the VM,
/// because that is what it would boot with.
public class CapacityRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_p1_vm_larger_than_the_whole_target_is_critical()
    {
        Finding finding = Assert.Single(Report(
            Startup("VM-DC-01", 10_240)).For(CheckRules.StartupRamExceedsTarget));

        Assert.Equal(Severity.Critical, finding.Rule.Severity);
        Assert.Contains("8192 MB usable", finding.Observed);
        Assert.Contains("cannot start on the target at all", finding.Implication);
    }

    /// Never acknowledgeable: accepting "this VM cannot start" is accepting that the failover
    /// does not work, which is the one thing the tool exists to say.
    [Fact]
    public void The_memory_criticals_can_never_be_acknowledged()
    {
        Assert.False(CheckRules.ById(CheckRules.StartupRamExceedsTarget)!.Acknowledgeable);
        Assert.False(
            CheckRules.ById(CheckRules.P1StartupRamSumExceedsTarget)!.Acknowledgeable);
    }

    /// The per-VM rule is about one VM against the whole target, not against what is left
    /// after the others: three VMs that each fit and together do not is the sum rule's case.
    [Fact]
    public void A_p1_vm_that_fits_on_its_own_is_not_reported_by_the_per_vm_rule()
    {
        Assert.Empty(Report(Startup("VM-DC-01", 8192))
            .For(CheckRules.StartupRamExceedsTarget));
    }

    /// A P2 VM that does not fit shows in the feasibility table as refused. It is not a
    /// critical: the second tier is what gets left behind on purpose.
    [Fact]
    public void A_p2_vm_larger_than_the_target_is_not_critical()
    {
        Assert.Empty(Report(Startup("VM-BACKUP-01", 10_240))
            .For(CheckRules.StartupRamExceedsTarget));
    }

    [Fact]
    public void P1_vms_that_together_exceed_the_target_are_critical()
    {
        CheckReport report = Report(
            Startup("VM-DC-01", 5120).WithTarget(
                "VM-LEGACY-01", facts => facts with { StartupRamMb = 5120 }));

        Finding finding = Assert.Single(
            report.For(CheckRules.P1StartupRamSumExceedsTarget));

        Assert.Equal(Severity.Critical, finding.Rule.Severity);
        Assert.Null(finding.Subject);
        Assert.Contains("10240 MB", finding.Observed);
    }

    [Fact]
    public void P1_vms_that_fit_together_produce_no_finding()
    {
        Assert.Empty(Report(Pairs.Healthy(Now))
            .For(CheckRules.P1StartupRamSumExceedsTarget));
    }

    /// One unreadable figure makes the sum unknown rather than smaller — an understated sum
    /// is how this critical silently passes.
    [Fact]
    public void One_unreadable_p1_figure_leaves_the_sum_unevaluable()
    {
        CheckReport report = Report(Startup("VM-DC-01", null));

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.P1StartupRamSumExceedsTarget)).Verdict);
    }

    /// A target whose memory could not be read cannot approve anything.
    [Fact]
    public void An_unreadable_target_leaves_every_memory_rule_unevaluable()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithTargetHost(facts => facts with { PhysicalRamMb = null }));

        Assert.All(
            report.For(CheckRules.StartupRamExceedsTarget),
            finding => Assert.Equal(FindingVerdict.Unevaluable, finding.Verdict));

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.P1StartupRamSumExceedsTarget)).Verdict);
    }

    /// It boots and is then squeezed (COHERENCE S5): a warning, because the operator has to
    /// know beforehand rather than during.
    [Fact]
    public void A_dynamic_maximum_beyond_the_target_is_a_warning()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTarget(
                "VM-DC-01", facts => facts with { DynamicMaximumMb = 16_384 }))
            .For(CheckRules.DynamicMaximumExceedsTarget));

        Assert.Equal(Severity.Warning, finding.Rule.Severity);
        Assert.Contains("squeezed to 1024 MB", finding.Observed);
        Assert.Contains("not necessarily usefully", finding.Implication);
    }

    [Fact]
    public void A_dynamic_maximum_inside_the_target_produces_no_finding()
    {
        Assert.Empty(Report(Pairs.Healthy(Now))
            .For(CheckRules.DynamicMaximumExceedsTarget));
    }

    /// An unread maximum is not a maximum inside the target: this rule was silent on missing
    /// data until the milestone 2 review pass caught it.
    [Fact]
    public void An_unreadable_dynamic_maximum_is_unevaluable_rather_than_silent()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithTarget(
                "VM-DC-01", facts => facts with { DynamicMaximumMb = null }));

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(
                report.For(CheckRules.DynamicMaximumExceedsTarget),
                finding => finding.Subject == "VM-DC-01").Verdict);
    }

    /// A maximum at or below the startup figure is a VM that cannot grow, dynamic memory on
    /// or off. Warning anyway would fire on every VM with static memory.
    [Fact]
    public void A_vm_that_cannot_grow_is_not_warned_about()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now).WithTarget("VM-DC-01", facts => facts with
            {
                StartupRamMb = 9216,
                DynamicMaximumMb = 9216,
            }))
            .For(CheckRules.DynamicMaximumExceedsTarget));
    }

    /// The configured figure was written in calm conditions; the observed one is the truth on
    /// the day (decision D5).
    [Fact]
    public void A_startup_figure_that_has_drifted_from_the_configuration_is_a_warning()
    {
        Finding finding = Assert.Single(Report(
            Startup("VM-DC-01", 3072),
            document => document.Vms![0].ExpectedStartupRamMb = 2048)
            .For(CheckRules.StartupRamDrift));

        Assert.Equal(Severity.Warning, finding.Rule.Severity);
        Assert.Contains("3072 MB", finding.Observed);
        Assert.Contains("2048 MB", finding.Observed);
    }

    /// Optional, so a configuration that does not state an expectation is not in drift.
    [Fact]
    public void With_no_configured_expectation_there_is_no_drift_rule()
    {
        Assert.Empty(Report(Startup("VM-DC-01", 3072)).For(CheckRules.StartupRamDrift));
    }

    [Fact]
    public void A_startup_figure_matching_the_configuration_produces_no_finding()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now),
            document => document.Vms![0].ExpectedStartupRamMb = 2048)
            .For(CheckRules.StartupRamDrift));
    }

    private static PairView Startup(string name, int? startupRamMb) =>
        Pairs.Healthy(Now).WithTarget(name, facts => facts with { StartupRamMb = startupRamMb });

    private static CheckReport Report(
        PairView view, Action<ConfigurationDocument>? adjust = null) =>
        Pairs.Evaluate(view, Now, adjust);
}
