using Ripcord.Adapters.Yaml;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Ports.Configuration;
using Ripcord.Ports;
using Ripcord.Tests.Architecture;

namespace Ripcord.Tests.Adapters;

/// The store translates, it does not decide: it reports only what stops a file being read at
/// all. Everything else is ConfigurationValidator's.
public sealed class YamlConfigStoreTests : IDisposable
{
    private readonly string directory =
        Directory.CreateTempSubdirectory("ripcord-config-tests").FullName;

    [Fact]
    public void Snake_case_keys_map_onto_the_document()
    {
        ConfigurationRead read = Read("""
            schema_version: 1
            node:
              hostname: HV-REPLICA-01
            peer:
              hostname: HV-PRIMARY-01
              address: 192.0.2.11
              offline_after_sec: 120
            vms:
              - name: VM-DC-01
                priority: P1
                is_domain_controller: true
              - name: VM-BACKUP-01
                priority: P2
                has_passthrough_disk: true
                expected_startup_ram_mb: 2048
            """);

        Assert.Empty(read.Errors);
        ConfigurationDocument document = Assert.IsType<ConfigurationDocument>(read.Document);
        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal("HV-REPLICA-01", document.Node!.Hostname);
        Assert.Equal(120, document.Peer!.OfflineAfterSec);
        Assert.True(document.Vms![0].IsDomainController);
        Assert.True(document.Vms[1].HasPassthroughDisk);
        Assert.Equal(2048, document.Vms[1].ExpectedStartupRamMb);
    }

    /// Sections a later milestone owns are ignored, not rejected: the shipped sample carries
    /// them, and refusing them would make the sample unusable.
    [Fact]
    public void Sections_this_milestone_does_not_read_are_ignored()
    {
        ConfigurationRead read = Read("""
            schema_version: 1
            node:
              hostname: HV-REPLICA-01
              host_memory_reserve_gb: 4
            storage:
              data_volume: "D:"
            alerts:
              smtp: { enabled: false }
            """);

        Assert.Empty(read.Errors);
        Assert.Equal("HV-REPLICA-01", read.Document!.Node!.Hostname);
    }

    /// A missing file is the likeliest first-run failure, and "unhandled exception" is not an
    /// error message anyone can act on.
    [Fact]
    public void A_missing_file_is_reported_by_path_not_by_exception()
    {
        ConfigurationRead read =
            new YamlConfigStore().Read(Path.Combine(this.directory, "absent.yaml"));

        Assert.Null(read.Document);
        ConfigurationError error = Assert.Single(read.Errors);

        // Absence, not a read failure. The path is the error's own field, so the CLI can say
        // it once and name the command that creates a file rather than repeating a .NET
        // sentence that says the path twice more and offers nothing.
        Assert.Equal(ConfigurationErrorKind.Missing, error.Kind);
        Assert.Contains("absent.yaml", error.Path, StringComparison.Ordinal);
    }

    [Fact]
    public void A_syntax_error_names_the_line()
    {
        ConfigurationRead read = Read("""
            schema_version: 1
            node:
                hostname: HV-REPLICA-01
              peer: broken
            """);

        Assert.Null(read.Document);
        Assert.Contains("line", Assert.Single(read.Errors).Message);
    }

    /// A file of comments parses to nothing at all, which must reach the validator as a
    /// missing document rather than as a document full of defaults.
    [Fact]
    public void An_empty_file_yields_no_document_and_no_read_error()
    {
        ConfigurationRead read = Read("# nothing here yet\n");

        Assert.Null(read.Document);
        Assert.Empty(read.Errors);
    }

    /// A scalar where a mapping belongs is a shape error, not a rule violation; the operator
    /// must still get a sentence rather than a stack trace.
    [Fact]
    public void A_field_of_the_wrong_type_is_reported_as_a_read_error()
    {
        ConfigurationRead read = Read("""
            schema_version: not-a-number
            node:
              hostname: HV-REPLICA-01
            """);

        Assert.Null(read.Document);
        Assert.NotEmpty(read.Errors);
    }

    /// The two shipped samples are part of the deliverable; a sample that does not validate
    /// is a broken one.
    [Theory]
    [InlineData("ripcord.dr.yaml", "HV-REPLICA-01")]
    [InlineData("ripcord.primary.yaml", "HV-PRIMARY-01")]
    public void The_shipped_sample_configurations_are_valid(string fileName, string machineName)
    {
        ConfigurationRead read = new YamlConfigStore()
            .Read(Path.Combine(RepositoryLayout.Root, "config", fileName));

        Assert.Empty(read.Errors);

        ConfigurationValidation validation =
            ConfigurationValidator.Validate(read.Document, machineName);

        Assert.Empty(validation.Errors);

        RipcordConfiguration configuration = validation.Configuration!;

        Assert.Equal(machineName, configuration.Node.Hostname);
        Assert.Equal(4, configuration.Node.HostMemoryReserveGb);
        Assert.Equal("vSwitch-PROD", configuration.Replication.ExpectedSwitchName);
        Assert.Equal("D:", configuration.Storage.DataVolume);

        // The sample carries the permanent critical of decision D20 and the one guest whose
        // support ends: both keys are optional, and a sample that stopped exercising them
        // would let them rot unnoticed.
        Assert.Equal(
            CheckRules.PassthroughDiskOnReplicatedVm,
            Assert.Single(configuration.Acknowledgements).RuleId);

        Assert.NotNull(
            configuration.Vms.Single(vm => vm.Name == "VM-LEGACY-01").GuestOsSupportEnds);

        // Decision D19, read through the real YAML naming convention rather than a hand-built
        // document: the backup VM is the reason the key exists, and a sample that lost it
        // would sweep the machine into every --all.
        Assert.Equal(
            FailoverPolicy.Manual,
            configuration.Vms.Single(vm => vm.Name == "VM-BACKUP-01").Failover);
    }

    /// The naming convention maps this key automatically, which is exactly why it needs a
    /// test: renaming the property would keep compiling and silently stop reading the file.
    [Fact]
    public void The_test_failover_switch_is_read_under_its_documented_name()
    {
        ConfigurationRead read = this.Read("""
            replication:
              expected_role: replica
              expected_switch_name: vSwitch-PROD
              expected_frequency_sec: 30
              lag_warning_multiplier: 3
              test_failover_switch: vSwitch-ISOLATED
            """);

        Assert.Equal("vSwitch-ISOLATED", read.Document!.Replication!.TestFailoverSwitch);
    }

    private ConfigurationRead Read(string yaml)
    {
        string path = Path.Combine(this.directory, "ripcord.yaml");
        File.WriteAllText(path, yaml);
        return new YamlConfigStore().Read(path);
    }

    /// `ripcord init` rewrites the file somebody's failovers depend on. The previous one is
    /// kept, and it is kept by name in the result so the console can say where it went.
    [Fact]
    public void Writing_keeps_the_previous_file_and_says_where()
    {
        string path = Path.Combine(this.directory, "ripcord.yaml");
        File.WriteAllText(path, "schema_version: 1\n");

        ConfigurationWrite written = new YamlConfigStore().Write(path, "node:\n", path + ".1");

        Assert.True(written.Written);
        Assert.Equal(path + ".1", written.Kept);
        Assert.Equal("node:\n", File.ReadAllText(path));
        Assert.Equal("schema_version: 1\n", File.ReadAllText(path + ".1"));
    }

    [Fact]
    public void A_first_write_keeps_nothing_because_there_was_nothing()
    {
        string path = Path.Combine(this.directory, "fresh.yaml");

        Assert.Null(new YamlConfigStore().Write(path, "node:\n", path + ".1").Kept);
        Assert.True(File.Exists(path));
    }

    /// The ordering that matters. The previous file used to be moved aside first, so a write
    /// that then failed left the host with **no** configuration at all while reporting only
    /// "cannot write". The content is staged whole before anything is moved.
    [Fact]
    public void A_write_that_cannot_complete_leaves_the_configuration_where_it_was()
    {
        string path = Path.Combine(this.directory, "ripcord.yaml");
        File.WriteAllText(path, "schema_version: 1\n");

        // The staging file's own path taken by a directory: the write fails before the
        // previous configuration has been touched.
        Directory.CreateDirectory(path + ".new");

        ConfigurationWrite written = new YamlConfigStore().Write(path, "node:\n", path + ".1");

        Assert.False(written.Written);
        Assert.NotNull(written.FailureMessage);
        Assert.Equal("schema_version: 1\n", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".1"));
    }

    [Fact]
    public void Reading_the_text_of_a_file_that_is_not_there_is_not_an_error() =>
        Assert.Null(new YamlConfigStore().ReadText(Path.Combine(this.directory, "absent.yaml")));

    public void Dispose() => Directory.Delete(this.directory, recursive: true);
}
