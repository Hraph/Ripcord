using Ripcord.Adapters.Yaml;
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
        Assert.Contains("absent.yaml", error.Message);
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
        Assert.Equal(machineName, validation.Configuration!.Node.Hostname);
    }

    private ConfigurationRead Read(string yaml)
    {
        string path = Path.Combine(this.directory, "ripcord.yaml");
        File.WriteAllText(path, yaml);
        return new YamlConfigStore().Read(path);
    }

    public void Dispose() => Directory.Delete(this.directory, recursive: true);
}
