using Ripcord.Domain.Configuration;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.TestFailover;

/// `test_failover_switch` is an optional addition to `schema_version: 1` (decision D12), so a
/// milestone 1 configuration keeps working unchanged. What it must never be is silently
/// wrong: absent means "disconnected only", and that is a usable default, but a key that is
/// present and useless has to be refused rather than degraded to the default.
public class TestFailoverSettingsTests
{
    [Fact]
    public void The_switch_is_absent_by_default_and_that_is_not_an_error()
    {
        RipcordConfiguration configuration = Valid(ValidDocument.Create());

        Assert.Null(configuration.Replication.TestFailoverSwitch);
    }

    [Fact]
    public void A_named_switch_is_carried_through()
    {
        Assert.Equal("vSwitch-ISOLATED", Valid(With("vSwitch-ISOLATED"))
            .Replication.TestFailoverSwitch);
    }

    /// The whole mechanism exists to put the test VM somewhere the production network is
    /// not. Pointing it at the production switch would satisfy the isolation rule while
    /// doing exactly what the rule forbids — the most dangerous possible value, and the one
    /// a copy-paste produces.
    [Fact]
    public void The_test_switch_may_not_be_the_expected_production_switch()
    {
        ConfigurationError error = Assert.Single(Errors(With("vSwitch-PROD")));

        Assert.Equal("replication.test_failover_switch", error.Path);
        Assert.Contains("expected_switch_name", error.Message);
    }

    [Fact]
    public void The_production_switch_is_refused_whatever_its_case()
    {
        Assert.NotEmpty(Errors(With("VSWITCH-prod")));
    }

    /// Present and blank is a typo, not an omission. Reading it as "absent" would hand back
    /// the disconnected-only default while the operator believes they configured a switch.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_switch_is_refused_rather_than_read_as_absent(string blank)
    {
        ConfigurationError error = Assert.Single(Errors(With(blank)));

        Assert.Equal("replication.test_failover_switch", error.Path);
    }

    /// Absent, a test VM is called an orphan after a day. Long enough that a slow test is
    /// never called one, short enough that a run interrupted overnight is reported the next
    /// morning rather than a week later.
    [Fact]
    public void The_orphan_threshold_defaults_to_a_day()
    {
        Assert.Equal(
            TimeSpan.FromHours(24),
            Valid(ValidDocument.Create()).Replication.TestFailoverOrphanAfter);
    }

    [Fact]
    public void A_declared_orphan_threshold_is_carried_through()
    {
        Assert.Equal(TimeSpan.FromHours(6), Valid(WithOrphanHours(6))
            .Replication.TestFailoverOrphanAfter);
    }

    /// Zero would report a running test failover as its own orphan on the first check.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_orphan_threshold_is_refused(int hours)
    {
        Assert.Equal(
            "replication.test_failover_orphan_after_hours",
            Assert.Single(Errors(WithOrphanHours(hours))).Path);
    }

    private static ConfigurationDocument WithOrphanHours(int hours)
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Replication!.TestFailoverOrphanAfterHours = hours;
        return document;
    }

    /// Unattended running is an authorisation, not a default. Absent, no VM may be tested
    /// without a human typing the node name.
    [Fact]
    public void No_vm_is_authorised_for_unattended_running_by_default()
    {
        Assert.Empty(Valid(ValidDocument.Create()).Replication.UnattendedTestFailoverVms);
    }

    [Fact]
    public void The_authorised_vms_are_carried_through()
    {
        Assert.Equal(
            ["VM-LEGACY-01"],
            Valid(WithUnattended("VM-LEGACY-01")).Replication.UnattendedTestFailoverVms);
    }

    /// A name that matches no VM is an operator who believes something is authorised when it
    /// is not — the same failure as a misspelt acknowledgement rule id.
    [Fact]
    public void An_authorised_vm_that_is_not_in_the_configuration_is_refused()
    {
        ConfigurationError error = Assert.Single(Errors(WithUnattended("VM-TYPO-01")));

        Assert.Equal("replication.unattended_test_failover_vms", error.Path);
        Assert.Contains("VM-TYPO-01", error.Message);
    }

    [Fact]
    public void A_blank_entry_is_refused()
    {
        Assert.NotEmpty(Errors(WithUnattended("  ")));
    }

    private static ConfigurationDocument WithUnattended(params string[] names)
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Replication!.UnattendedTestFailoverVms = [.. names];
        return document;
    }

    private static ConfigurationDocument With(string testFailoverSwitch)
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Replication!.TestFailoverSwitch = testFailoverSwitch;
        return document;
    }

    private static RipcordConfiguration Valid(ConfigurationDocument document) =>
        Assert.IsType<RipcordConfiguration>(Validate(document).Configuration);

    private static IReadOnlyList<ConfigurationError> Errors(ConfigurationDocument document) =>
        Validate(document).Errors;

    private static ConfigurationValidation Validate(ConfigurationDocument document) =>
        ConfigurationValidator.Validate(document, ValidDocument.MachineName);
}
