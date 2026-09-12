using Ripcord.Domain.Configuration;
using Ripcord.Domain.Pairing;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Pairing;

/// Turning configuration into "who to call and who to accept". A Domain decision, so it is
/// tested without a socket.
public class PeerEndpointTests
{
    private const string Local = ValidDocument.LocalThumbprint;
    private const string Peer = ValidDocument.PeerThumbprint;

    [Fact]
    public void An_enabled_listener_yields_an_endpoint_naming_both_ends()
    {
        PeerEndpoint endpoint = Assert.IsType<PeerEndpoint>(PeerEndpoint.From(Configuration()));

        Assert.Equal("192.0.2.11", endpoint.Address);
        Assert.Equal(7443, endpoint.Port);
        Assert.Equal(Peer, endpoint.Rules.ExpectedThumbprint);
        Assert.Equal(Local, endpoint.LocalCertificateThumbprint);
        Assert.Equal("CN=HV-PRIMARY-01", endpoint.Rules.ExpectedSubject);
        Assert.Equal("192.0.2.11", endpoint.Rules.ExpectedAddress);
    }

    /// The documented off switch. A node with the listener disabled does not go looking for
    /// the peer either — it degrades to the local-only view.
    [Fact]
    public void A_disabled_listener_yields_no_endpoint()
    {
        Assert.Null(PeerEndpoint.From(Configuration(enabled: false)));
    }

    /// One attempt may not hang for as long as it takes to call a peer offline: those are
    /// different durations, and conflating them would hang `status` for two minutes.
    [Fact]
    public void The_attempt_timeout_is_not_the_offline_threshold()
    {
        PeerEndpoint endpoint = PeerEndpoint.From(Configuration())!;

        Assert.True(endpoint.Timeout < TimeSpan.FromSeconds(120));
        Assert.True(endpoint.Timeout > TimeSpan.Zero);
    }

    private static RipcordConfiguration Configuration(bool enabled = true) =>
        Configurations.Create(document => document.Listener!.Enabled = enabled);
}
