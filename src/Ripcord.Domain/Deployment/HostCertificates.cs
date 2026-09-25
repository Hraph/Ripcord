using Ripcord.Domain.Pairing;

namespace Ripcord.Domain.Deployment;

/// A certificate in `LocalMachine\My` that has a private key, as the adapter found it.
public sealed record HostCertificate(string Subject, string Thumbprint, DateTimeOffset NotAfter);

/// This host's certificates the other host would accept by subject: what to paste into the two
/// `ripcord.yaml` files. Read without the configuration, which is the file being filled in.
public sealed record HostCertificates(IReadOnlyList<HostCertificate> Usable, string Subject, string? Unreadable)
{
    /// `CN=<this host>`, as the peer checks it, not expired, latest expiry first.
    public static HostCertificates For(
        IReadOnlyList<HostCertificate> found, string machineName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(found);

        string subject = $"CN={machineName}";

        return new HostCertificates(
            [.. found
                .Where(certificate => PeerIdentity.SameName(certificate.Subject, subject)
                    && certificate.NotAfter > now)
                .OrderByDescending(certificate => certificate.NotAfter)],
            subject,
            null);
    }

    public static HostCertificates CouldNotRead(string machineName, string reason) =>
        new([], $"CN={machineName}", reason);

    public bool Equals(HostCertificates? other) =>
        other is not null
        && this.Subject == other.Subject
        && this.Unreadable == other.Unreadable
        && Structural.Same(this.Usable, other.Usable);

    public override int GetHashCode() => HashCode.Combine(this.Subject, this.Unreadable);
}
