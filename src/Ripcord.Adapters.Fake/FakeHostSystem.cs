using Ripcord.Domain.Inventory;
using Ripcord.Ports.Hosts;

namespace Ripcord.Adapters.Fake;

/// The host outside Hyper-V, scripted. The failing variant matters as much as the healthy
/// one: a host whose `root\cimv2` cannot be read must degrade to unknown facts, never to a
/// confident zero.
public sealed class FakeHostSystemProvider : IHostSystemProvider
{
    private readonly HostSystemReading reading;
    private readonly Exception? failure;

    public FakeHostSystemProvider(HostSystemReading reading) => this.reading = reading;

    private FakeHostSystemProvider(Exception failure)
    {
        this.reading = HostSystemReading.Unknown();
        this.failure = failure;
    }

    public static FakeHostSystemProvider Failing(string message) =>
        new(new InvalidOperationException(message));

    /// The target of the real pair: roughly 12 GB and a BitLocker-protected `D:` that does
    /// unlock itself.
    public static FakeHostSystemProvider Target(long freeBytes = 500_000_000_000) =>
        new(new HostSystemReading(
            12_288,
            [
                new HostVolume("C:", 80_000_000_000, 240_000_000_000, false, null),
                new HostVolume("D:", freeBytes, 2_000_000_000_000, true, true),
            ]));

    public static FakeHostSystemProvider Primary() =>
        new(new HostSystemReading(
            49_152,
            [
                new HostVolume("C:", 120_000_000_000, 240_000_000_000, false, null),
                new HostVolume("D:", 1_200_000_000_000, 4_000_000_000_000, false, null),
            ]));

    public Task<HostSystemReading> ReadAsync(CancellationToken cancellationToken) =>
        this.failure is null
            ? Task.FromResult(this.reading)
            : Task.FromException<HostSystemReading>(this.failure);
}

/// The certificate store without a store. Absent by default: a thumbprint that names nothing
/// is the case the rule has to report rather than crash on.
public sealed class FakeCertificateProvider(params CertificateFact[] certificates)
    : ICertificateProvider
{
    private readonly Exception? failure;

    private FakeCertificateProvider(Exception failure)
        : this([]) =>
        this.failure = failure;

    public static FakeCertificateProvider Failing(string message) =>
        new(new InvalidOperationException(message));

    /// Expiring on the date the real pair's certificates do.
    public static FakeCertificateProvider Valid(string thumbprint, string commonName) =>
        new(new CertificateFact(
            thumbprint, commonName, new DateTimeOffset(2029, 9, 1, 0, 0, 0, TimeSpan.Zero)));

    public static FakeCertificateProvider Expiring(
        string thumbprint, string commonName, DateTimeOffset notAfter) =>
        new(new CertificateFact(thumbprint, commonName, notAfter));

    public IReadOnlyList<CertificateFact> WithPrivateKey() =>
        this.failure is not null ? throw this.failure : certificates;

    public CertificateFact? Find(string thumbprint) =>
        this.failure is not null
            ? throw this.failure
            : certificates.FirstOrDefault(certificate => string.Equals(
                certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));
}
