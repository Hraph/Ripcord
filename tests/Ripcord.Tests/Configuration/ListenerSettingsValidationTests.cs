using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Configuration;

/// The `listener` block and the two thumbprints become validated at milestone 1b because
/// milestone 1b reads them. The rule is the one D23 states: validate what a user can get
/// wrong today, reserve what nothing reads yet.
public class ListenerSettingsValidationTests
{
    private const string MachineName = ValidDocument.MachineName;

    private const string LocalThumbprint = ValidDocument.LocalThumbprint;
    private const string PeerThumbprint = ValidDocument.PeerThumbprint;

    /// A node with no listener block degrades to the milestone 1 local-only view. That is the
    /// documented off switch, so it must not be an error.
    [Fact]
    public void A_configuration_with_no_listener_block_is_valid_and_disabled()
    {
        ConfigurationDocument document = Valid();
        document.Listener = null;

        ConfigurationValidation result = Validate(document);

        Assert.Empty(result.Errors);
        Assert.False(result.Configuration!.Listener.Enabled);
    }

    [Fact]
    public void An_enabled_listener_yields_its_port_and_both_thumbprints()
    {
        ConfigurationValidation result = Validate(Valid());

        Assert.Empty(result.Errors);
        ListenerSettings listener = result.Configuration!.Listener;
        Assert.True(listener.Enabled);
        Assert.Equal(7443, listener.Port);
        Assert.Equal(LocalThumbprint, listener.LocalCertificateThumbprint);
        Assert.Equal(PeerThumbprint, listener.PeerCertificateThumbprint);
    }

    /// Without both ends identified there is no mutual authentication, so an enabled listener
    /// missing a thumbprint is refused rather than started in a weaker mode.
    [Theory]
    [InlineData("listener.local_certificate_thumbprint")]
    [InlineData("listener.peer_certificate_thumbprint")]
    public void An_enabled_listener_without_both_thumbprints_is_refused(string path)
    {
        ConfigurationDocument document = Valid();

        if (path.Contains("local", StringComparison.Ordinal))
        {
            document.Listener!.LocalCertificateThumbprint = null;
        }
        else
        {
            document.Listener!.PeerCertificateThumbprint = null;
        }

        AssertError(Validate(document), path);
    }

    /// A disabled listener is not asked for credentials it will never present.
    [Fact]
    public void A_disabled_listener_needs_no_thumbprints()
    {
        ConfigurationDocument document = Valid();
        document.Listener!.Enabled = false;
        document.Listener.LocalCertificateThumbprint = null;
        document.Listener.PeerCertificateThumbprint = null;

        Assert.Empty(Validate(document).Errors);
    }

    /// A thumbprint is 40 hex characters. Anything else is a paste accident, and it would
    /// surface as a handshake failure at 3 a.m. rather than as a config error now.
    [Theory]
    [InlineData("AAAA")]
    [InlineData("GGGG1111BBBB2222CCCC3333DDDD4444EEEE5555")]
    [InlineData("AAAA1111BBBB2222CCCC3333DDDD4444EEEE55555")]
    public void A_malformed_thumbprint_is_refused(string thumbprint)
    {
        ConfigurationDocument document = Valid();
        document.Listener!.LocalCertificateThumbprint = thumbprint;

        AssertError(Validate(document), "listener.local_certificate_thumbprint");
    }

    /// Well-formed, so without this they would pass and the channel fail as a SILENT peer.
    [Theory]
    [InlineData("AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555")]
    [InlineData("11 11 aa aa 22 22 bb bb 33 33 cc cc 44 44 dd dd 55 55 ee ee")]
    public void The_samples_placeholder_thumbprints_are_refused_by_name(string thumbprint)
    {
        ConfigurationDocument document = Valid();
        document.Listener!.LocalCertificateThumbprint = thumbprint;

        ConfigurationError error = Assert.Single(Validate(document).Errors);
        Assert.Equal("listener.local_certificate_thumbprint", error.Path);
        Assert.Contains("sample's placeholder", error.Message, StringComparison.Ordinal);
        Assert.Contains("this host's certificate", error.Message, StringComparison.Ordinal);
    }

    /// `enabled: false` is how a host runs before its certificates exist; the sample's
    /// placeholders behind it must not stop `status` or `failover` from loading.
    [Fact]
    public void Behind_a_disabled_listener_the_placeholders_load_as_absent()
    {
        ConfigurationDocument document = Valid();
        document.Listener!.Enabled = false;
        document.Listener.LocalCertificateThumbprint = ListenerSettings.SamplePlaceholders[0];
        document.Listener.PeerCertificateThumbprint = ListenerSettings.SamplePlaceholders[1];

        ConfigurationValidation result = Validate(document);

        Assert.Empty(result.Errors);
        Assert.Null(result.Configuration!.Listener.LocalCertificateThumbprint);
        Assert.Null(result.Configuration.Listener.PeerCertificateThumbprint);
    }

    /// Windows certificate tooling copies thumbprints with spaces and in either case; both
    /// paste forms are the same certificate.
    [Theory]
    [InlineData("a1b2c3d4e5f60718293a4b5c6d7e8f9012345678")]
    [InlineData("A1 B2 C3 D4 E5 F6 07 18 29 3A 4B 5C 6D 7E 8F 90 12 34 56 78")]
    public void A_thumbprint_is_normalised_rather_than_rejected_for_its_formatting(
        string thumbprint)
    {
        ConfigurationDocument document = Valid();
        document.Listener!.LocalCertificateThumbprint = thumbprint;

        ConfigurationValidation result = Validate(document);

        Assert.Empty(result.Errors);
        Assert.Equal(LocalThumbprint, result.Configuration!.Listener.LocalCertificateThumbprint);
    }

    /// The same certificate on both ends would authenticate a host to itself.
    [Fact]
    public void The_two_thumbprints_must_differ()
    {
        ConfigurationDocument document = Valid();
        document.Listener!.PeerCertificateThumbprint = LocalThumbprint;

        AssertError(Validate(document), "listener.peer_certificate_thumbprint");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65_536)]
    public void A_port_outside_the_usable_range_is_refused(int port)
    {
        ConfigurationDocument document = Valid();
        document.Listener!.Port = port;

        AssertError(Validate(document), "listener.port");
    }

    /// The snapshot file is what the service serves. Its path is the one thing the deploy
    /// command has to grant the service account access to, so it has a default rather than
    /// being required.
    ///
    /// The default is beside the configuration file — which is beside the binary, with the
    /// log, the audit trail and the alert state. A default on another volume is one that does
    /// not exist on a host without that volume, and `icacls` cannot grant access to a path
    /// that is not there.
    [Theory]
    [InlineData(@"C:\Program Files\Ripcord\ripcord.yaml", @"C:\Program Files\Ripcord\state.json")]
    [InlineData(@"D:\ripcord.yaml", @"D:\state.json")]
    [InlineData("ripcord.yaml", "state.json")]
    public void The_snapshot_path_defaults_beside_the_configuration(string config, string expected)
    {
        ConfigurationDocument document = Valid();
        document.Listener!.SnapshotPath = null;

        ConfigurationValidation result =
            ConfigurationValidator.Validate(document, MachineName, config);

        Assert.Empty(result.Errors);
        Assert.Equal(expected, result.Configuration!.Listener.SnapshotPath);
    }

    private static ConfigurationValidation Validate(ConfigurationDocument document) =>
        ConfigurationValidator.Validate(document, MachineName);

    private static void AssertError(ConfigurationValidation result, string path)
    {
        Assert.Null(result.Configuration);
        Assert.Contains(path, result.Errors.Select(error => error.Path));
    }

    private static ConfigurationDocument Valid() => ValidDocument.Create();

    /// Access is granted on the folder, so there has to be one. A path without it resolves
    /// against the working directory of whoever runs the command — and a Windows service's is
    /// `system32`, not the folder the operator was picturing.
    [Theory]
    [InlineData("state.json")]
    [InlineData("D:state.json")]
    public void A_snapshot_path_naming_no_folder_is_refused(string path)
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Listener!.SnapshotPath = path;

        ConfigurationValidation validation = ConfigurationValidator.Validate(
            document, ValidDocument.MachineName, @"D:\Ripcord\ripcord.yaml");

        Assert.Contains(
            validation.Errors,
            error => error.Path == "listener.snapshot_path"
                && error.Message.Contains("must name a folder", StringComparison.Ordinal));
    }

    /// Only what the operator wrote. A listener that is switched off is never deployed, so
    /// there is no folder to grant anything on and nothing to refuse.
    [Fact]
    public void A_disabled_listener_is_not_refused_for_a_path_nothing_will_use()
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Listener!.Enabled = false;
        document.Listener.SnapshotPath = "state.json";

        Assert.DoesNotContain(
            ConfigurationValidator.Validate(document, ValidDocument.MachineName).Errors,
            error => error.Path == "listener.snapshot_path");
    }

    /// Respected, not rewritten: an operator who set the old default on a host that has D:
    /// keeps it. Whether the volume exists is a host fact `service install` checks.
    [Fact]
    public void An_explicit_snapshot_path_is_kept_as_written()
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Listener!.SnapshotPath = @"D:\Ripcord\state.json";

        ConfigurationValidation validation = ConfigurationValidator.Validate(
            document, ValidDocument.MachineName, @"C:\Program Files\Ripcord\ripcord.yaml");

        Assert.Equal(@"D:\Ripcord\state.json", validation.Configuration!.Listener.SnapshotPath);
    }

    [Theory]
    [InlineData(@"D:\Ripcord\state.json", true)]
    [InlineData("d:/ripcord/STATE.json", true)]
    [InlineData(@"C:\Program Files\Ripcord\state.json", false)]
    [InlineData(null, false)]
    public void The_old_default_is_recognised_however_it_is_spelt(string? path, bool legacy) =>
        Assert.Equal(legacy, ListenerSettings.IsLegacyDefault(path));
}
