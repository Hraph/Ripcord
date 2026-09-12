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
                CommonName(certificate),
                chainTrusted,
                now < certificate.NotBefore || now > certificate.NotAfter);

    /// The CN only, not the whole distinguished name: a CA-issued certificate normally carries
    /// more relative names (O, OU, C), and comparing the full DN against the `CN=<host>` the
    /// configuration implies would refuse every real certificate.
    private static string CommonName(X509Certificate2 certificate)
    {
        foreach (X500RelativeDistinguishedName name in
            certificate.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            if (name.GetSingleElementType().FriendlyName == "CN"
                && name.GetSingleElementValue() is { } value)
            {
                return $"CN={value}";
            }
        }

        return certificate.Subject;
    }
}
