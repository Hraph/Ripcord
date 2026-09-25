using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Deployment;

/// The certificates the other host would accept from this one, by the subject it checks.
public class HostCertificatesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 22, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_this_hosts_subject_and_unexpired_latest_first()
    {
        HostCertificate older = new("CN=HV-DR-01", "AA", Now.AddMonths(2));
        HostCertificate newer = new("cn = hv-dr-01", "BB", Now.AddYears(2));

        HostCertificates certificates = HostCertificates.For(
            [
                older,
                new HostCertificate("CN=HV-DR-01", "CC", Now.AddDays(-1)),
                new HostCertificate("CN=HV-PRIMARY-01", "DD", Now.AddYears(1)),
                new HostCertificate("CN=HV-DR-01.corp.example", "EE", Now.AddYears(1)),
                newer,
            ],
            "HV-DR-01",
            Now);

        Assert.Equal([newer, older], certificates.Usable);
        Assert.Equal("CN=HV-DR-01", certificates.Subject);
        Assert.Null(certificates.Unreadable);
    }
}
