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

    /// These facts, with the BitLocker state an earlier snapshot of this host held wherever this
    /// read could not see it.
    public HostFacts WithBitLockerFrom(HostFacts? previous, DateTimeOffset previousCapturedAt) =>
        previous is null
            ? this
            : this with
            {
                Volumes = [.. this.Volumes.Select(volume =>
                    volume.WithBitLockerFrom(previous.Volume(volume.Name), previousCapturedAt))],
            };

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
/// `BitLockerReadAt` is set when those two were carried from an earlier read rather than read
/// now: the publishing service may not be allowed to read them, an administrator was.
public sealed record HostVolume(
    string Name,
    long? FreeBytes,
    long? TotalBytes,
    bool? IsBitLockerProtected,
    bool? IsAutoUnlockEnabled,
    DateTimeOffset? BitLockerReadAt = null)
{
    /// The BitLocker state of `previous` where this read has none, dated from when it was
    /// read. Nothing is carried onto a volume read now, nor invented where none was ever read.
    public HostVolume WithBitLockerFrom(HostVolume? previous, DateTimeOffset previousCapturedAt) =>
        this.IsBitLockerProtected is null && previous?.IsBitLockerProtected is { } isProtected
            ? this with
            {
                IsBitLockerProtected = isProtected,
                IsAutoUnlockEnabled = previous.IsAutoUnlockEnabled,
                BitLockerReadAt = previous.BitLockerReadAt ?? previousCapturedAt,
            }
            : this;
}

/// The host's own listener certificate, found by thumbprint (decision D6). The CN is carried
/// alongside so the consistency check the configuration implies can still be made.
public sealed record CertificateFact(string Thumbprint, string CommonName, DateTimeOffset NotAfter)
{
    public bool HasExpiredAt(DateTimeOffset now) => now >= this.NotAfter;

    /// An already expired certificate is inside every window rather than outside them.
    public bool ExpiresWithin(TimeSpan window, DateTimeOffset now) =>
        this.NotAfter - now <= window;
}
