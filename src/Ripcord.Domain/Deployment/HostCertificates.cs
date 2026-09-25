using Ripcord.Domain.Inventory;
using Ripcord.Domain.Pairing;

namespace Ripcord.Domain.Deployment;

/// This host's certificates the other host would accept by subject: what `ripcord pair` needs
/// on both sides. Read without the configuration, which is the file being filled in.
public sealed record HostCertificates(IReadOnlyList<CertificateFact> Usable, string MachineName, string? Unreadable)
{
    public string Subject => $"CN={this.MachineName}";

    /// `CN=<this host>`, compared as the peer compares it, not expired, latest expiry first.
    public static HostCertificates For(
        IReadOnlyList<CertificateFact> found, string machineName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(found);

        string subject = $"CN={machineName}";

        return new HostCertificates(
            [.. found
                .Where(certificate => PeerIdentity.SameName(certificate.CommonName, subject)
                    && !certificate.HasExpiredAt(now))
                .OrderByDescending(certificate => certificate.NotAfter)],
            machineName,
            null);
    }

    public static HostCertificates CouldNotRead(string machineName, string reason) =>
        new([], machineName, reason);

    /// Whether the configured thumbprint is one of these; the configuration holds it normalised.
    public bool Lists(string? thumbprint) =>
        thumbprint is not null
        && this.Usable.Any(certificate =>
            string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));

    public bool Equals(HostCertificates? other) =>
        other is not null
        && this.MachineName == other.MachineName
        && this.Unreadable == other.Unreadable
        && Structural.Same(this.Usable, other.Usable);

    public override int GetHashCode() => HashCode.Combine(this.MachineName, this.Unreadable);
}
