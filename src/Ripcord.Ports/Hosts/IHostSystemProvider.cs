using Ripcord.Domain.Inventory;

namespace Ripcord.Ports.Hosts;

/// The host outside Hyper-V (COHERENCE B4): installed memory and the volumes, from
/// `root\cimv2` and `root\cimv2\Security\MicrosoftVolumeEncryption`. None of it is in
/// `root\virtualization\v2`, which is why it is a port of its own rather than a method on
/// `IHypervProvider`.
///
/// Every volume is returned, not the configured one: which volume matters is a decision, and
/// decisions live in the Domain.
public interface IHostSystemProvider
{
    Task<HostSystemReading> ReadAsync(CancellationToken cancellationToken);
}

/// Null memory and an empty volume list are both "not read", never "none" — a host reporting
/// no memory must not read as a host with no capacity.
public sealed record HostSystemReading(int? PhysicalRamMb, IReadOnlyList<HostVolume> Volumes)
{
    public static HostSystemReading Unknown() => new(null, []);
}
