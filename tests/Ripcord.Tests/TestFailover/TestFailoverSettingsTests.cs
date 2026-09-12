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
