using Ripcord.Domain.Pairing;

namespace Ripcord.Tests.Pairing;

/// Who is allowed to be on the other end of the channel. The rule lives in the Domain and not
/// in the TLS callback, because "chain validation AND name validation, neither alone is
/// enough" is a decision, and a decision buried in a callback is one nobody tests.
public class PeerIdentityTests
{
    private const string Expected = "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555";

    [Fact]
    public void The_expected_certificate_from_the_expected_host_is_accepted()
    {
        Assert.Equal(PeerVerdict.Accepted, Verify());
    }

    /// A trusted chain proves the certificate is genuine, not that it belongs to the peer. The
    /// pair's own CA signs both hosts, so chain-only would let either impersonate the other.
    [Fact]
    public void A_valid_chain_with_the_wrong_thumbprint_is_refused()
    {
        Assert.Equal(
            PeerVerdict.WrongCertificate,
            Verify(thumbprint: "1111AAAA2222BBBB3333CCCC4444DDDD5555EEEE"));
    }

    /// And the reverse: the right thumbprint over an untrusted chain means someone replayed a
    /// public certificate they do not hold the key for, or the CA is not the one we think.
    [Fact]
    public void The_expected_thumbprint_with_an_untrusted_chain_is_refused()
    {
        Assert.Equal(PeerVerdict.UntrustedChain, Verify(chainTrusted: false));
    }

    [Fact]
    public void An_expired_certificate_is_refused_even_if_it_is_the_right_one()
    {
        Assert.Equal(PeerVerdict.Expired, Verify(expired: true));
    }

    /// The subject is checked in addition to the thumbprint (decision D6). It cannot catch
    /// anything the thumbprint misses, but a mismatch means the config and the PKI disagree,
    /// and that is worth refusing loudly rather than discovering during a failover.
    [Fact]
    public void A_certificate_whose_subject_is_not_the_peer_is_refused()
    {
        Assert.Equal(PeerVerdict.WrongSubject, Verify(subject: "CN=SOMEONE-ELSE"));
    }

    [Fact]
    public void The_subject_is_matched_case_insensitively_and_ignoring_spacing()
    {
        Assert.Equal(PeerVerdict.Accepted, Verify(subject: "cn = hv-primary-01"));
    }

    /// A certificate presented from an address that is not the peer's is refused even when it
    /// is the peer's certificate: a stolen key used from elsewhere is the case this catches.
    [Fact]
    public void The_right_certificate_from_the_wrong_address_is_refused()
    {
        Assert.Equal(PeerVerdict.WrongAddress, Verify(remoteAddress: "192.0.2.99"));
    }

    /// No certificate at all is the commonest probe, and must not be mistaken for a
    /// connection problem.
    [Fact]
    public void A_connection_presenting_no_certificate_is_refused()
    {
        Assert.Equal(
            PeerVerdict.NoCertificate,
            PeerIdentity.Verify(null, Rules(), "192.0.2.11"));
    }

    /// Every refusal names one reason, so the service log says which of the five it was
    /// rather than "handshake failed".
    [Fact]
    public void Every_verdict_other_than_accepted_states_a_reason()
    {
        foreach (PeerVerdict verdict in Enum.GetValues<PeerVerdict>())
        {
            Assert.False(string.IsNullOrWhiteSpace(verdict.Reason()));
        }
    }

    /// A dual-stack listener reports an IPv4 peer as `::ffff:192.0.2.11`. That is the same
    /// host the configuration names, and refusing it would take the pair view down on a
    /// network change rather than on an attack.
    [Fact]
    public void The_peer_arriving_over_ipv6_from_its_own_address_is_still_the_peer() =>
        Assert.Equal(PeerVerdict.Accepted, Verify(remoteAddress: "::ffff:192.0.2.11"));

    [Fact]
    public void Another_address_is_still_refused() =>
        Assert.Equal(PeerVerdict.WrongAddress, Verify(remoteAddress: "::ffff:192.0.2.12"));

    private static PeerVerdict Verify(
        string thumbprint = Expected,
        string subject = "CN=HV-PRIMARY-01",
        bool chainTrusted = true,
        bool expired = false,
        string remoteAddress = "192.0.2.11") =>
        PeerIdentity.Verify(
            new PresentedCertificate(thumbprint, subject, chainTrusted, IsExpired: expired),
            Rules(),
            remoteAddress);

    private static PeerRules Rules() => new(Expected, "CN=HV-PRIMARY-01", "192.0.2.11");
}
