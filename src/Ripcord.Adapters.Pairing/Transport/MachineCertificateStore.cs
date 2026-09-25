using System.Security.Cryptography.X509Certificates;
using Ripcord.Domain.Inventory;
using Ripcord.Ports.Hosts;

namespace Ripcord.Adapters.Pairing.Transport;

/// `LocalMachine\My`, by thumbprint (decision D6): a subject lookup can return several
/// certificates, including an expired one, and pick the wrong one. The service account has no
/// user store of its own, so the machine store is the only one there is.
///
/// Nothing is cached. A certificate renewed under a running service is then picked up without
/// a restart — which matters for a listener that may run for months.
public sealed class MachineCertificateStore : ICertificateProvider
{
    /// For the TLS handshake: a certificate with no private key cannot serve or present.
    public static X509Certificate2 WithPrivateKey(string thumbprint)
    {
        X509Certificate2? found = Search(
            thumbprint, certificate => certificate.HasPrivateKey);

        return found
            ?? throw new InvalidOperationException(
                $"no certificate with thumbprint {thumbprint} and a private key in "
                + @"LocalMachine\My");
    }

    /// For the expiry rule: the private key is irrelevant to when a certificate expires, and
    /// requiring it would report a configured certificate as absent.
    ///
    /// Null when nothing carries that thumbprint. That is the rule's finding to report, not
    /// an exception for the command to fail on.
    public CertificateFact? Find(string thumbprint)
    {
        using X509Certificate2? certificate = Search(thumbprint, _ => true);

        return certificate is null
            ? null
            : new CertificateFact(
                certificate.Thumbprint,
                CertificateFacts.CommonName(certificate),

                // NotAfter is a local-kind DateTime; the conversion keeps the instant rather
                // than re-reading it as UTC, which would move the expiry by the host's offset.
                certificate.NotAfter);
    }

    public IReadOnlyList<CertificateFact> WithPrivateKey()
    {
        using X509Store store = new(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);

        List<CertificateFact> found = [];

        foreach (X509Certificate2 certificate in store.Certificates)
        {
            using (certificate)
            {
                if (certificate.HasPrivateKey)
                {
                    found.Add(new CertificateFact(
                        certificate.Thumbprint, CertificateFacts.CommonName(certificate), certificate.NotAfter));
                }
            }
        }

        return found;
    }

    private static X509Certificate2? Search(
        string thumbprint, Func<X509Certificate2, bool> accept)
    {
        using X509Store store = new(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);

        X509Certificate2? found = null;

        foreach (X509Certificate2 candidate in store.Certificates)
        {
            if (found is null
                && string.Equals(
                    candidate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)
                && accept(candidate))
            {
                found = candidate;
            }
            else
            {
                // Every certificate the store handed over is disposed, not only the one kept.
                candidate.Dispose();
            }
        }

        return found;
    }
}
