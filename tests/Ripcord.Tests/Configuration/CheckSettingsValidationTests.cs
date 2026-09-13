using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Configuration;

/// The sections `ripcord check` reads. They are required rather than defaulted: a missing
/// host reserve would silently overstate the target's usable memory, and an overstated
/// capacity is the reassuring false negative this tool exists to prevent.
public class CheckSettingsValidationTests
{
    private const string MachineName = ValidDocument.MachineName;

    [Fact]
    public void The_milestone_two_sections_yield_settings()
    {
        RipcordConfiguration configuration = Assert.IsType<RipcordConfiguration>(
            Validate(ValidDocument.Create()).Configuration);

        Assert.Equal(4, configuration.Node.HostMemoryReserveGb);
        Assert.Equal(ExpectedRole.Replica, configuration.Replication.ExpectedRole);
        Assert.Equal("vSwitch-PROD", configuration.Replication.ExpectedSwitchName);
        Assert.Equal(TimeSpan.FromSeconds(30), configuration.Replication.ExpectedFrequency);
        Assert.Equal(3, configuration.Replication.LagWarningMultiplier);
        Assert.Equal("D:", configuration.Storage.DataVolume);
        Assert.Equal(200, configuration.Storage.FreeSpaceWarningGb);
        Assert.True(configuration.Storage.CheckBitLockerAutoUnlock);
    }

    /// A reserve of zero claims the management OS needs no memory, which is never true and
    /// would let the feasibility calculation approve a failover that cannot boot.
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1024)]
    public void The_host_memory_reserve_is_required_and_bounded(int? reserve)
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Node!.HostMemoryReserveGb = reserve;

        AssertError(Validate(document), "node.host_memory_reserve_gb");
    }

    [Fact]
    public void A_missing_replication_section_reports_the_section()
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Replication = null;

        Assert.Equal(["replication"], Validate(document).Errors.Select(error => error.Path));
    }

    [Theory]
    [InlineData(null, 30, 3, "replication.expected_switch_name")]
    [InlineData("  ", 30, 3, "replication.expected_switch_name")]
    [InlineData("vSwitch-PROD", null, 3, "replication.expected_frequency_sec")]
    [InlineData("vSwitch-PROD", 0, 3, "replication.expected_frequency_sec")]
    [InlineData("vSwitch-PROD", 30, null, "replication.lag_warning_multiplier")]
    [InlineData("vSwitch-PROD", 30, 0, "replication.lag_warning_multiplier")]
    // The upper bounds, which are not opinions: a frequency longer than a day and a multiplier
    // past which the lag rule can never fire are both ways of switching a rule off by a large
    // number rather than by name.
    [InlineData("vSwitch-PROD", 86_401, 3, "replication.expected_frequency_sec")]
    [InlineData("vSwitch-PROD", 30, 1_001, "replication.lag_warning_multiplier")]
    public void The_replication_fields_the_rules_read_are_required(
        string? switchName, int? frequency, int? multiplier, string path)
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Replication = new ReplicationDocument
        {
            ExpectedRole = "replica",
            ExpectedSwitchName = switchName,
            ExpectedFrequencySec = frequency,
            LagWarningMultiplier = multiplier,
        };

        AssertError(Validate(document), path);
    }

    /// A threshold nobody sets still has to have a value, and the default is the one the
    /// report prints — so it is stated once, here, rather than inside the rule.
    [Fact]
    public void The_health_threshold_defaults_when_absent_and_is_read_when_present()
    {
        ConfigurationDocument document = ValidDocument.Create();

        Assert.Equal(
            ReplicationSettings.DefaultHealthWarningAfter,
            Validate(document).Configuration!.Replication.HealthWarningAfter);

        document.Replication!.HealthWarningAfterSec = 900;

        Assert.Equal(
            TimeSpan.FromMinutes(15),
            Validate(document).Configuration!.Replication.HealthWarningAfter);
    }

    /// Read by name rather than by ordinal: "0" must never become Primary.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("source")]
    public void The_expected_role_is_required_and_named(string? role)
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Replication!.ExpectedRole = role;

        AssertError(Validate(document), "replication.expected_role");
    }

    [Theory]
    [InlineData("primary", ExpectedRole.Primary)]
    [InlineData("REPLICA", ExpectedRole.Replica)]
    public void The_expected_role_is_read_in_either_case(string role, ExpectedRole expected)
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Replication!.ExpectedRole = role;

        Assert.Equal(expected, Validate(document).Configuration!.Replication.ExpectedRole);
    }

    [Fact]
    public void A_missing_storage_section_reports_the_section()
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Storage = null;

        Assert.Equal(["storage"], Validate(document).Errors.Select(error => error.Path));
    }

    /// The volume is matched against what Windows reports and printed in the remedy command,
    /// so it has to be a drive rather than any string.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D")]
    [InlineData(@"D:\Ripcord")]
    [InlineData("data")]
    public void The_data_volume_must_name_a_drive(string? volume)
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Storage!.DataVolume = volume;

        AssertError(Validate(document), "storage.data_volume");
    }

    [Theory]
    [InlineData("D:")]
    [InlineData("d:")]
    [InlineData(@"D:\")]
    public void A_drive_is_accepted_in_the_spellings_windows_uses(string volume)
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Storage!.DataVolume = volume;

        Assert.Empty(Validate(document).Errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1_000_001)]
    public void The_free_space_threshold_is_required_and_positive(int? threshold)
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Storage!.FreeSpaceWarningGb = threshold;

        AssertError(Validate(document), "storage.free_space_warning_gb");
    }

    /// A guest whose support has ended is an info finding, and the date is the only source —
    /// nothing in WMI knows when Microsoft stops shipping patches.
    [Fact]
    public void A_guest_support_end_date_is_optional_and_read_as_utc_when_present()
    {
        ConfigurationDocument document = ValidDocument.Create();

        Assert.Null(Validate(document).Configuration!.Vms[1].GuestOsSupportEnds);

        document.Vms![1].GuestOsSupportEnds = new DateTime(2026, 10, 13);

        Assert.Equal(
            new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero),
            Validate(document).Configuration!.Vms[1].GuestOsSupportEnds);
    }

    [Fact]
    public void No_checks_section_means_no_acknowledgement_rather_than_an_error()
    {
        RipcordConfiguration configuration = Validate(ValidDocument.Create()).Configuration!;

        Assert.Empty(configuration.Acknowledgements);
    }

    [Fact]
    public void An_acknowledgement_yields_its_rule_scope_reason_and_expiry()
    {
        Acknowledgement acknowledgement = Assert.Single(
            Validate(WithAcknowledgement(Acknowledged())).Configuration!.Acknowledgements);

        Assert.Equal(CheckRules.PassthroughDiskOnReplicatedVm, acknowledgement.RuleId);
        Assert.Equal("VM-BACKUP-01", acknowledgement.VmName);
        Assert.Equal("the backup repository lives on it", acknowledgement.Reason);
        Assert.Equal(
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), acknowledgement.Expires);
    }

    /// A misspelt rule id is an operator who believes a finding is suppressed when it is not.
    [Fact]
    public void An_acknowledgement_of_an_unknown_rule_is_refused()
    {
        AcknowledgementDocument acknowledgement = Acknowledged();
        acknowledgement.Rule = "passthru-disk";

        AssertError(
            Validate(WithAcknowledgement(acknowledgement)), "checks.acknowledgements[0].rule");
    }

    /// The separate class: silencing "this VM cannot boot on the target" silences the tool.
    [Fact]
    public void A_rule_that_must_never_be_acknowledged_is_refused()
    {
        AcknowledgementDocument acknowledgement = Acknowledged();
        acknowledgement.Rule = CheckRules.P1StartupRamSumExceedsTarget;

        AssertError(
            Validate(WithAcknowledgement(acknowledgement)), "checks.acknowledgements[0].rule");
    }

    /// An acknowledgement with no end date is a rule deleted by the back door.
    [Fact]
    public void An_acknowledgement_without_an_expiry_is_refused()
    {
        AcknowledgementDocument acknowledgement = Acknowledged();
        acknowledgement.Expires = null;

        AssertError(
            Validate(WithAcknowledgement(acknowledgement)), "checks.acknowledgements[0].expires");
    }

    [Fact]
    public void An_acknowledgement_without_a_reason_is_refused()
    {
        AcknowledgementDocument acknowledgement = Acknowledged();
        acknowledgement.Reason = "  ";

        AssertError(
            Validate(WithAcknowledgement(acknowledgement)), "checks.acknowledgements[0].reason");
    }

    /// Scoped to a VM that is not declared, it suppresses nothing and says otherwise.
    [Fact]
    public void An_acknowledgement_scoped_to_an_undeclared_vm_is_refused()
    {
        AcknowledgementDocument acknowledgement = Acknowledged();
        acknowledgement.Vm = "VM-GHOST-01";

        AssertError(
            Validate(WithAcknowledgement(acknowledgement)), "checks.acknowledgements[0].vm");
    }

    /// Host-scoped: no VM at all is how a host-level rule is acknowledged.
    [Fact]
    public void An_acknowledgement_with_no_vm_covers_the_whole_host()
    {
        AcknowledgementDocument acknowledgement = Acknowledged();
        acknowledgement.Rule = CheckRules.FreeSpaceBelowThreshold;
        acknowledgement.Vm = null;

        Assert.Null(
            Assert.Single(Validate(WithAcknowledgement(acknowledgement))
                .Configuration!.Acknowledgements).VmName);
    }

    /// An expiry already in the past is valid configuration that has simply lapsed. Refusing
    /// it would make the whole command exit 2 on a date nobody chose — the finding comes back
    /// instead, which is the point of the expiry.
    [Fact]
    public void An_expiry_already_past_is_accepted_and_reported_as_lapsed()
    {
        AcknowledgementDocument document = Acknowledged();
        document.Expires = new DateTime(2020, 1, 1);

        Acknowledgement acknowledgement = Assert.Single(
            Validate(WithAcknowledgement(document)).Configuration!.Acknowledgements);

        Assert.False(acknowledgement.IsActiveAt(new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void The_same_rule_and_scope_acknowledged_twice_is_refused()
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Checks = new ChecksDocument
        {
            Acknowledgements = [Acknowledged(), Acknowledged()],
        };

        AssertError(Validate(document), "checks.acknowledgements[1].rule");
    }

    private static AcknowledgementDocument Acknowledged() => new()
    {
        Rule = CheckRules.PassthroughDiskOnReplicatedVm,
        Vm = "VM-BACKUP-01",
        Reason = "the backup repository lives on it",
        Expires = new DateTime(2027, 1, 1),
    };

    private static ConfigurationDocument WithAcknowledgement(AcknowledgementDocument entry)
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Checks = new ChecksDocument { Acknowledgements = [entry] };
        return document;
    }

    private static ConfigurationValidation Validate(ConfigurationDocument document) =>
        ConfigurationValidator.Validate(document, MachineName);

    private static void AssertError(ConfigurationValidation result, string path)
    {
        Assert.Null(result.Configuration);
        Assert.Contains(path, result.Errors.Select(error => error.Path));
    }
}
