using System.Security.Cryptography.X509Certificates;
using Ripcord.Domain.Pairing;

namespace Ripcord.Adapters.Pairing.Transport;

/// X509 to the four facts PeerIdentity judges. Extraction only — every "is this acceptable"
/// question is answered in the Domain, where a test can reach it.
internal static class CertificateFacts
{
    public static PresentedCertificate? Describe(
        X509Certificate2? certificate, bool chainTrusted, DateTimeOffset now) =>
        certificate is null
            ? null
            : new PresentedCertificate(
                certificate.Thumbprint,
                certificate.Subject,
                chainTrusted,
                now < certificate.NotBefore || now > certificate.NotAfter);
}
