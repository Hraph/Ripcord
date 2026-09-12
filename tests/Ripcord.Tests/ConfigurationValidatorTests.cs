using Ripcord.Domain.Configuration;

namespace Ripcord.Tests;

/// Validation answers "can this file be read?", never "does reality match this file?" —
/// that line is milestone 2's. It also reports every error at once: re-running six times to
/// discover six typos is not what anyone wants at 3 a.m.
public class ConfigurationValidatorTests
{
    private const string MachineName = "HV-REPLICA-01";

    [Fact]
    public void A_well_formed_document_validates_and_yields_a_configuration()
    {
        ConfigurationValidation result = Validate(Valid());

        Assert.Empty(result.Errors);
        RipcordConfiguration configuration = Assert.IsType<RipcordConfiguration>(result.Configuration);
        Assert.Equal("HV-REPLICA-01", configuration.Node.Hostname);
        Assert.Equal("HV-PRIMARY-01", configuration.Peer.Hostname);
        Assert.Equal(TimeSpan.FromSeconds(120), configuration.Peer.OfflineAfter);
        Assert.Equal(3, configuration.Vms.Count);
    }

    [Fact]
    public void An_empty_file_is_rejected_rather_than_treated_as_defaults()
    {
        ConfigurationValidation result = ConfigurationValidator.Validate(null, MachineName);

        Assert.Null(result.Configuration);
        Assert.Contains(result.Errors, error => error.Message.Contains("empty"));
    }

    /// A version this binary does not know is refused outright. Guessing at an unknown schema
    /// is how a tool ends up acting on a field it has misread.
    [Fact]
    public void An_unknown_schema_version_is_refused_never_guessed()
    {
        ConfigurationDocument document = Valid();
        document.SchemaVersion = 2;

        AssertError(Validate(document), "schema_version");
    }

    [Fact]
    public void A_missing_schema_version_is_an_error()
    {
        ConfigurationDocument document = Valid();
        document.SchemaVersion = null;

        AssertError(Validate(document), "schema_version");
    }

    /// The failure mode this exists for: a config copied from the primary to the target
    /// without swapping the blocks yields a tool that believes it is on the other host.
    [Fact]
    public void Node_hostname_must_match_the_machine_it_runs_on()
    {
        ConfigurationValidation result =
            ConfigurationValidator.Validate(Valid(), machineName: "HV-PRIMARY-01");

        AssertError(result, "node.hostname");
    }

    [Fact]
    public void Node_hostname_matching_is_case_insensitive()
    {
        Assert.Empty(ConfigurationValidator.Validate(Valid(), machineName: "hv-replica-01").Errors);
    }

    [Fact]
    public void Peer_hostname_must_differ_from_the_node_hostname()
    {
        ConfigurationDocument document = Valid();
        document.Peer!.Hostname = "hv-replica-01";

        AssertError(Validate(document), "peer.hostname");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(86_401)]
    public void Peer_offline_threshold_must_be_a_sane_duration(int seconds)
    {
        ConfigurationDocument document = Valid();
        document.Peer!.OfflineAfterSec = seconds;

        AssertError(Validate(document), "peer.offline_after_sec");
    }

    [Fact]
    public void A_vm_priority_outside_the_declared_set_is_rejected()
    {
        ConfigurationDocument document = Valid();
        document.Vms![0].Priority = "urgent";

        AssertError(Validate(document), "vms[0].priority");
    }

    [Fact]
    public void Vm_priorities_are_read_case_insensitively()
    {
        ConfigurationDocument document = Valid();
        document.Vms![0].Priority = "p1";

        ConfigurationValidation result = Validate(document);

        Assert.Empty(result.Errors);
        Assert.Equal(VmPriority.P1, result.Configuration!.Vms[0].Priority);
    }

    /// Enum.TryParse accepts the underlying integer as a string, so "1" would parse as P2 —
    /// the second tier for an operator who meant the first, with no error and a reordered
    /// failover at milestone 4. Only the spelled names are a priority.
    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("+1")]
    [InlineData("2")]
    public void A_numeric_priority_is_rejected_rather_than_read_as_an_enum_ordinal(string priority)
    {
        ConfigurationDocument document = Valid();
        document.Vms![0].Priority = priority;

        AssertError(Validate(document), "vms[0].priority");
    }

    /// Two blocks for one VM means one of them is silently ignored, and nobody finds out
    /// which until a failover skips a machine.
    [Fact]
    public void A_vm_declared_twice_is_rejected()
    {
        ConfigurationDocument document = Valid();
        document.Vms![1].Name = document.Vms[0].Name;

        AssertError(Validate(document), "vms[1].name");
    }

    [Fact]
    public void A_configuration_with_no_vms_is_rejected()
    {
        ConfigurationDocument document = Valid();
        document.Vms = [];

        AssertError(Validate(document), "vms");
    }

    /// Optional and unused at this milestone (decision D5) — but a value that is present and
    /// absurd is still a typo worth reporting.
    [Fact]
    public void Expected_startup_ram_is_optional_but_must_be_positive_when_present()
    {
        ConfigurationDocument document = Valid();
        document.Vms![0].ExpectedStartupRamMb = 0;

        AssertError(Validate(document), "vms[0].expected_startup_ram_mb");

        document.Vms[0].ExpectedStartupRamMb = null;
        Assert.Empty(Validate(document).Errors);
    }

    /// The whole point of collecting rather than failing fast.
    [Fact]
    public void Every_error_is_reported_in_one_pass()
    {
        ConfigurationDocument document = new()
        {
            SchemaVersion = 9,
            Node = new NodeDocument { Hostname = "  " },
            Peer = new PeerDocument { Hostname = null, Address = null, OfflineAfterSec = 0 },
            Vms = [new VmDocument { Name = null, Priority = "P4" }],
        };

        string[] paths = Validate(document).Errors.Select(error => error.Path).ToArray();

        Assert.Equal(
            [
                "schema_version", "node.hostname", "peer.hostname", "peer.address",
                "peer.offline_after_sec", "vms[0].name", "vms[0].priority",
            ],
            paths);
    }

    [Fact]
    public void A_missing_section_reports_the_section_not_each_of_its_fields()
    {
        ConfigurationDocument document = Valid();
        document.Peer = null;

        ConfigurationValidation result = Validate(document);

        Assert.Equal(["peer"], result.Errors.Select(error => error.Path));
    }

    private static ConfigurationValidation Validate(ConfigurationDocument document) =>
        ConfigurationValidator.Validate(document, MachineName);

    private static void AssertError(ConfigurationValidation result, string path)
    {
        Assert.Null(result.Configuration);
        Assert.Contains(path, result.Errors.Select(error => error.Path));
    }

    private static ConfigurationDocument Valid() => new()
    {
        SchemaVersion = 1,
        Node = new NodeDocument { Hostname = "HV-REPLICA-01" },
        Peer = new PeerDocument
        {
            Hostname = "HV-PRIMARY-01",
            Address = "192.0.2.11",
            OfflineAfterSec = 120,
        },
        Vms =
        [
            new VmDocument { Name = "VM-DC-01", Priority = "P1", IsDomainController = true },
            new VmDocument { Name = "VM-LEGACY-01", Priority = "P1" },
            new VmDocument { Name = "VM-BACKUP-01", Priority = "P2", HasPassthroughDisk = true },
        ],
    };
}
