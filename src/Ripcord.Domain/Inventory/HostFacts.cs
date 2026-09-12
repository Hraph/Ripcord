namespace Ripcord.Domain.Inventory;

/// What the host itself offers, as opposed to what its VMs ask for. None of it comes from
/// `root\virtualization\v2` (COHERENCE B4), and all of it is read on both hosts: the
/// feasibility calculation is about the *target*, which is the peer.
public sealed record HostFacts(
    int? PhysicalRamMb,
    IReadOnlyList<HostVolume> Volumes,
    CertificateFact? Certificate)
{
    public static HostFacts Unknown() => new(null, [], null);

    /// Physical RAM less what the management OS must keep for itself — the one figure in the
    /// feasibility calculation that comes from configuration (decision D5).
    /// Clamped at zero: a reserve larger than the machine is a misconfiguration to report,
    /// not a negative capacity to propagate.
    public int? UsableRamMb(int hostReserveGb) =>
        this.PhysicalRamMb is { } physical
            ? Math.Max(0, physical - (hostReserveGb * 1024))
            : null;

    /// `storage.data_volume` is written "D:" by hand while Windows reports "D:" or "D:\"
    /// depending on the class. The operator should not have to know which.
    public HostVolume? Volume(string name) =>
        this.Volumes.FirstOrDefault(volume => DriveOf(volume.Name) == DriveOf(name));

    private static string DriveOf(string name) =>
        name.TrimEnd('\\', '/').ToUpperInvariant();

    public bool Equals(HostFacts? other) =>
        other is not null
        && this.PhysicalRamMb == other.PhysicalRamMb
        && this.Certificate == other.Certificate
        && Structural.Same(this.Volumes, other.Volumes);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(this.PhysicalRamMb);
        hash.Add(this.Certificate);
        Structural.Add(ref hash, this.Volumes);
        return hash.ToHashCode();
    }
}

/// One volume. `IsBitLockerProtected` and `IsAutoUnlockEnabled` are null when the encryption
/// namespace could not be read — which is not "unencrypted", and must not read as it.
public sealed record HostVolume(
    string Name,
    long? FreeBytes,
    long? TotalBytes,
    bool? IsBitLockerProtected,
    bool? IsAutoUnlockEnabled);

/// The host's own listener certificate, found by thumbprint (decision D6). The CN is carried
/// alongside so the consistency check the configuration implies can still be made.
public sealed record CertificateFact(string Thumbprint, string CommonName, DateTimeOffset NotAfter)
{
    public bool HasExpiredAt(DateTimeOffset now) => now >= this.NotAfter;

    /// An already expired certificate is inside every window rather than outside them.
    public bool ExpiresWithin(TimeSpan window, DateTimeOffset now) =>
        this.NotAfter - now <= window;
}
