using Ripcord.Domain.Configuration;
using Ripcord.Domain.Pairing;

namespace Ripcord.Tests.Pairing;

/// Turning configuration into "who to call and who to accept". A Domain decision, so it is
/// tested without a socket.
public class PeerEndpointTests
{
    private const string Local = "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555";
    private const string Peer = "1111AAAA2222BBBB3333CCCC4444DDDD5555EEEE";

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
        new(
            new NodeSettings("HV-REPLICA-01"),
            new PeerSettings("HV-PRIMARY-01", "192.0.2.11", TimeSpan.FromSeconds(120)),
            enabled
                ? new ListenerSettings(true, 7443, Local, Peer, "state.json")
                : ListenerSettings.Disabled(),
            [new VmSettings("VM-DC-01", VmPriority.P1, false, false, null)]);
}
