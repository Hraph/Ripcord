namespace Ripcord.Domain.Pairing;

/// What the other end presented, reduced to the four facts the decision needs. The adapter
/// extracts these from an X509Certificate2 and a chain status; it does not judge them.
public sealed record PresentedCertificate(
    string Thumbprint,
    string Subject,
    bool ChainTrusted,
    bool IsExpired);

/// Who this node will accept. Thumbprints identify (decision D6); the subject is checked in
/// addition; the address restricts where the certificate may be used from.
public sealed record PeerRules(
    string ExpectedThumbprint,
    string ExpectedSubject,
    string ExpectedAddress);

public enum PeerVerdict
{
    Accepted,
    NoCertificate,
    UntrustedChain,
    Expired,
    WrongCertificate,
    WrongSubject,
    WrongAddress,
}

/// Chain validation **and** name validation, per the specification: neither alone is enough.
/// The pair's own CA signs both hosts, so a trusted chain proves the certificate is genuine,
/// not that it belongs to the peer — chain-only would let either host impersonate the other.
/// The reverse matters too: the right thumbprint over an untrusted chain means a public
/// certificate replayed by someone who does not hold its key.
///
/// This lives in the Domain rather than in the TLS callback because it is a decision, and a
/// decision buried in a callback is one nobody tests.
public static class PeerIdentity
{
    public static PeerVerdict Verify(
        PresentedCertificate? presented, PeerRules rules, string remoteAddress)
    {
        ArgumentNullException.ThrowIfNull(rules);

        if (presented is null)
        {
            return PeerVerdict.NoCertificate;
        }

        if (!presented.ChainTrusted)
        {
            return PeerVerdict.UntrustedChain;
        }

        if (presented.IsExpired)
        {
            return PeerVerdict.Expired;
        }

        if (!Same(presented.Thumbprint, rules.ExpectedThumbprint))
        {
            return PeerVerdict.WrongCertificate;
        }

        if (!SameName(presented.Subject, rules.ExpectedSubject))
        {
            return PeerVerdict.WrongSubject;
        }

        // A stolen key used from anywhere but the peer's address is the case this catches.
        return Same(remoteAddress, rules.ExpectedAddress)
            ? PeerVerdict.Accepted
            : PeerVerdict.WrongAddress;
    }

    /// One sentence per refusal, so the service log says which check failed rather than
    /// "handshake failed".
    public static string Reason(this PeerVerdict verdict) => verdict switch
    {
        PeerVerdict.Accepted => "the peer's certificate and address both match",
        PeerVerdict.NoCertificate => "the caller presented no client certificate",
        PeerVerdict.UntrustedChain => "the certificate chain is not trusted",
        PeerVerdict.Expired => "the certificate is outside its validity dates",
        PeerVerdict.WrongCertificate => "the certificate is not the peer's",
        PeerVerdict.WrongSubject => "the certificate subject is not the peer's",
        _ => "the certificate did not come from the peer's address",
    };

    private static bool Same(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    /// Distinguished names differ in spacing between tools; only the content is meaningful.
    private static bool SameName(string left, string right) =>
        Same(left.Replace(" ", "", StringComparison.Ordinal),
            right.Replace(" ", "", StringComparison.Ordinal));
}
