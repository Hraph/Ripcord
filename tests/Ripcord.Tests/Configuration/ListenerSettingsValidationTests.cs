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

    /// Windows certificate tooling copies thumbprints with spaces and in either case; both
    /// paste forms are the same certificate.
    [Theory]
    [InlineData("aaaa1111bbbb2222cccc3333dddd4444eeee5555")]
    [InlineData("AA AA 11 11 BB BB 22 22 CC CC 33 33 DD DD 44 44 EE EE 55 55")]
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
    [Fact]
    public void The_snapshot_path_defaults_when_it_is_not_given()
    {
        ConfigurationDocument document = Valid();
        document.Listener!.SnapshotPath = null;

        ConfigurationValidation result = Validate(document);

        Assert.Empty(result.Errors);
        Assert.False(string.IsNullOrWhiteSpace(result.Configuration!.Listener.SnapshotPath));
    }

    private static ConfigurationValidation Validate(ConfigurationDocument document) =>
        ConfigurationValidator.Validate(document, MachineName);

    private static void AssertError(ConfigurationValidation result, string path)
    {
        Assert.Null(result.Configuration);
        Assert.Contains(path, result.Errors.Select(error => error.Path));
    }

    private static ConfigurationDocument Valid() => ValidDocument.Create();
}
