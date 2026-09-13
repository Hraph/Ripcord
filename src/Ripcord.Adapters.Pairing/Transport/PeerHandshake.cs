using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Ripcord.Domain.Pairing;

namespace Ripcord.Adapters.Pairing.Transport;

/// Which roots a peer certificate must chain to. Empty means the machine's trust store, which
/// is where the pair's replication CA lives on the real hosts; the tests pass their own root
/// so the handshake can be exercised without installing anything.
public sealed record PeerTrust(IReadOnlyList<X509Certificate2> Roots)
{
    public static PeerTrust MachineStore { get; } = new([]);
}

/// Builds the TLS validation callback. It extracts facts and hands them to PeerIdentity; it
/// decides nothing itself.
///
/// The framework's own name check is deliberately not relied on: modern validation matches
/// the SAN, while the specification and the pair's certificates identify hosts by CN. So the
/// name is matched in the Domain, against the subject the configuration names, and a SAN
/// mismatch here is not on its own a reason to refuse. The thumbprint is what identifies.
internal static class PeerHandshake
{
    public static RemoteCertificateValidationCallback Validator(
        PeerRules rules,
        PeerTrust trust,
        string remoteAddress,
        DateTimeOffset now,
        Action<PeerVerdict> record) =>
        (_, certificate, _, errors) =>
        {
            X509Certificate2? presented = certificate as X509Certificate2;

            PeerVerdict verdict = PeerIdentity.Verify(
                CertificateFacts.Describe(presented, ChainIsTrusted(presented, trust, errors), now),
                rules,
                remoteAddress);

            record(verdict);
            return verdict == PeerVerdict.Accepted;
        };

    /// With custom roots the chain is rebuilt against them: trusting the machine store when
    /// the pair has its own CA would let any public authority sign a peer certificate.
    ///
    /// Two policy choices, both deliberate. **Revocation is not checked**: the pair's CA is
    /// its own and publishes no CRL anywhere these hosts can reach, and a revocation check
    /// that cannot complete either fails the handshake or is ignored — neither is worth the
    /// ambiguity. Pinning is the revocation mechanism here: a compromised certificate is
    /// retired by changing `peer_certificate_thumbprint` on the other host. **Validity dates
    /// are ignored by the chain build** so that an expired certificate reaches PeerIdentity
    /// and is refused as `Expired`, by name, instead of arriving as an untrusted chain the
    /// operator then has to diagnose.
    private static bool ChainIsTrusted(
        X509Certificate2? certificate, PeerTrust trust, SslPolicyErrors errors)
    {
        if (certificate is null)
        {
            return false;
        }

        if (trust.Roots.Count == 0)
        {
            return !errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors);
        }

        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;

        foreach (X509Certificate2 root in trust.Roots)
        {
            chain.ChainPolicy.CustomTrustStore.Add(root);
        }

        return chain.Build(certificate);
    }
}
