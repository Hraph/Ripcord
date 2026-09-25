using Ripcord.Domain.Deployment;
using Ripcord.Domain.Inventory;

namespace Ripcord.Tests.Deployment;

/// The certificates the other host would accept from this one. The CN arrives already taken out
/// of the distinguished name by the adapter the peer's check uses too, so the two cannot differ.
public class HostCertificatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_this_hosts_subject_and_unexpired_latest_first()
    {
        CertificateFact older = new("AA", "CN=HV-DR-01", Now.AddMonths(2));
        CertificateFact newer = new("BB", "cn = hv-dr-01", Now.AddYears(2));

        HostCertificates certificates = HostCertificates.For(
            [
                older,
                new CertificateFact("CC", "CN=HV-DR-01", Now.AddDays(-1)),
                new CertificateFact("DD", "CN=HV-PRIMARY-01", Now.AddYears(1)),

                // The peer expects `CN=<peer.hostname>`, the short name: an FQDN is refused there.
                new CertificateFact("EE", "CN=HV-DR-01.corp.example", Now.AddYears(1)),
                newer,
            ],
            "HV-DR-01",
            Now);

        Assert.Equal([newer, older], certificates.Usable);
        Assert.Equal("CN=HV-DR-01", certificates.Subject);
        Assert.Null(certificates.Unreadable);
    }

    [Fact]
    public void A_configured_thumbprint_is_found_whatever_its_case()
    {
        HostCertificates certificates = HostCertificates.For(
            [new CertificateFact("AA", "CN=HV-DR-01", Now.AddYears(1))], "HV-DR-01", Now);

        Assert.True(certificates.Lists("aa"));
        Assert.False(certificates.Lists("BB"));
        Assert.False(certificates.Lists(null));
    }
}
