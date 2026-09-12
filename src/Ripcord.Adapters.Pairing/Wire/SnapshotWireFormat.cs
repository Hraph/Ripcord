using System.Text.Json;
using System.Text.Json.Serialization;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;

namespace Ripcord.Adapters.Pairing.Wire;

/// The one payload the pair channel carries. Deserialising it is the only thing the listener
/// does with network input, so the shape is fixed and flat: no polymorphism, no type names,
/// no constructors chosen by the payload, and a size cap applied before parsing.
///
/// Reading never throws. This runs inside a network-facing service, where an exception
/// escaping the parser is precisely the bug the threat model is about.
public static class SnapshotWireFormat
{
    /// Three VMs serialise to well under a kilobyte. A megabyte is room to grow and still a
    /// bound on what a caller can make the service hold.
    public const int MaxPayloadBytes = 1024 * 1024;

    /// The *wire* format's version, which is not `ripcord.yaml`'s `schema_version` and moves
    /// independently of it. Version 2 adds the host and VM facts the cross-host rules compare;
    /// version 1 is still read — a host mid-update publishes it, and its facts arrive absent.
    internal const int WireFormatVersion = 2;

    internal static readonly int[] ReadableWireFormatVersions = [1, 2];

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Write(HostSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // A host that cannot read its own Hyper-V must not publish a confident empty
        // inventory; it publishes nothing and the peer reports it unreachable.
        if (!snapshot.State.IsReachable)
        {
            throw new ArgumentException(
                "an unreachable host has no state to publish", nameof(snapshot));
        }

        return JsonSerializer.Serialize(SnapshotPayload.From(snapshot), Options);
    }

    public static HostSnapshot? Read(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaxPayloadBytes)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SnapshotPayload>(payload, Options)?.ToSnapshot();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// Flat, nullable, and separate from the domain model on purpose: the wire shape is a
/// contract with the other host and must not move when the model does.
internal sealed record SnapshotPayload
{
    /// Serialises as `schema_version`, which is what a deployed milestone 1b peer already
    /// sends — the key cannot be renamed without breaking that pair. It carries the *wire*
    /// format's version, unrelated to `ripcord.yaml`'s own `schema_version`.
    public int SchemaVersion { get; init; }

    public DateTimeOffset? CapturedAt { get; init; }

    public string? HostName { get; init; }

    public List<VmPayload>? Vms { get; init; }

    public HostFactsPayload? Facts { get; init; }

    public static SnapshotPayload From(HostSnapshot snapshot) => new()
    {
        SchemaVersion = SnapshotWireFormat.WireFormatVersion,
        CapturedAt = snapshot.CapturedAt,
        HostName = snapshot.State.HostName,
        Vms = [.. snapshot.State.Vms.Select(VmPayload.From)],
        Facts = HostFactsPayload.From(snapshot.State.Facts),
    };

    /// Anything missing, unknown or out of range yields null rather than a partly built
    /// snapshot: a peer view assembled from half a payload is worse than no peer view.
    public HostSnapshot? ToSnapshot()
    {
        if (!SnapshotWireFormat.ReadableWireFormatVersions.Contains(this.SchemaVersion)
            || this.CapturedAt is not { } capturedAt
            || string.IsNullOrWhiteSpace(this.HostName)
            || this.Vms is null)
        {
            return null;
        }

        List<VmReplicationState> vms = [];

        foreach (VmPayload vm in this.Vms)
        {
            if (vm.ToState() is not { } state)
            {
                return null;
            }

            vms.Add(state);
        }

        return new HostSnapshot(
            capturedAt,
            new HostState(
                this.HostName, vms, HostReachability.Reachable(), this.Facts?.ToFacts()));
    }
}

internal sealed record VmPayload
{
    public string? Name { get; init; }

    public int Role { get; init; }

    public int State { get; init; }

    public int Health { get; init; }

    public DateTimeOffset? LastReplicationTime { get; init; }

    public long? PendingBytes { get; init; }

    public VmFactsPayload? Facts { get; init; }

    public static VmPayload From(VmReplicationState vm) => new()
    {
        Name = vm.Name,
        Role = (int)vm.Role,
        State = (int)vm.State,
        Health = (int)vm.Health,
        LastReplicationTime = vm.LastReplicationTime,
        PendingBytes = vm.PendingBytes,
        Facts = VmFactsPayload.From(vm.Facts),
    };

    /// Enums cross as numbers, and a number the other side does not know maps to its Unknown
    /// rather than to whatever happens to sit at that ordinal.
    public VmReplicationState? ToState() =>
        string.IsNullOrWhiteSpace(this.Name)
            ? null
            : new VmReplicationState(
                this.Name,
                Defined<ReplicationRole>(this.Role, ReplicationRole.Unknown),
                Defined<ReplicationState>(this.State, ReplicationState.Unknown),
                Defined<ReplicationHealth>(this.Health, ReplicationHealth.Unknown),
                this.LastReplicationTime,
                this.PendingBytes,
                this.Facts?.ToFacts());

    private static T Defined<T>(int value, T fallback)
        where T : struct, Enum =>
        Enum.IsDefined((T)(object)value) ? (T)(object)value : fallback;
}

/// Absent entirely on a schema 1 payload, and absent field by field on a host that could not
/// read part of its own configuration. Neither case may turn into a zero.
internal sealed record VmFactsPayload
{
    public int? StartupRamMb { get; init; }

    public int? DynamicMaximumMb { get; init; }

    public int? DynamicMinimumMb { get; init; }

    public List<AdapterPayload>? Adapters { get; init; }

    public List<DiskPayload>? Disks { get; init; }

    public List<string>? ReplicatedDiskPaths { get; init; }

    public static VmFactsPayload? From(VmFacts? facts) =>
        facts is null
            ? null
            : new VmFactsPayload
            {
                StartupRamMb = facts.StartupRamMb,
                DynamicMaximumMb = facts.DynamicMaximumMb,
                DynamicMinimumMb = facts.DynamicMinimumMb,
                Adapters = [.. facts.Adapters.Select(AdapterPayload.From)],
                Disks = [.. facts.Disks.Select(DiskPayload.From)],
                ReplicatedDiskPaths = facts.ReplicatedDiskPaths is { } paths ? [.. paths] : null,
            };

    public VmFacts ToFacts() =>
        new(
            this.StartupRamMb,
            this.DynamicMaximumMb,
            this.DynamicMinimumMb,
            [.. (this.Adapters ?? []).Select(adapter => adapter.ToAdapter())],
            [.. (this.Disks ?? []).Select(disk => disk.ToDisk())],
            this.ReplicatedDiskPaths);
}

internal sealed record AdapterPayload
{
    public string? Name { get; init; }

    public string? SwitchName { get; init; }

    public bool? IsConnected { get; init; }

    public string? MacAddress { get; init; }

    public bool? UsesDynamicMac { get; init; }

    public int? VlanId { get; init; }

    public static AdapterPayload From(VirtualAdapter adapter) => new()
    {
        Name = adapter.Name,
        SwitchName = adapter.SwitchName,
        IsConnected = adapter.IsConnected,
        MacAddress = adapter.MacAddress,
        UsesDynamicMac = adapter.UsesDynamicMac,
        VlanId = adapter.VlanId,
    };

    public VirtualAdapter ToAdapter() =>
        new(
            this.Name ?? "",
            this.SwitchName,
            this.IsConnected,
            this.MacAddress,
            this.UsesDynamicMac,
            this.VlanId);
}

internal sealed record DiskPayload
{
    public string? Path { get; init; }

    public bool IsPassthrough { get; init; }

    public static DiskPayload From(VmDisk disk) => new()
    {
        Path = disk.Path,
        IsPassthrough = disk.IsPassthrough,
    };

    public VmDisk ToDisk() => new(this.Path ?? "", this.IsPassthrough);
}

internal sealed record HostFactsPayload
{
    public int? PhysicalRamMb { get; init; }

    public List<VolumePayload>? Volumes { get; init; }

    public CertificatePayload? Certificate { get; init; }

    public static HostFactsPayload? From(HostFacts? facts) =>
        facts is null
            ? null
            : new HostFactsPayload
            {
                PhysicalRamMb = facts.PhysicalRamMb,
                Volumes = [.. facts.Volumes.Select(VolumePayload.From)],
                Certificate = CertificatePayload.From(facts.Certificate),
            };

    public HostFacts ToFacts() =>
        new(
            this.PhysicalRamMb,
            [.. (this.Volumes ?? []).Select(volume => volume.ToVolume())],
            this.Certificate?.ToFact());
}

internal sealed record VolumePayload
{
    public string? Name { get; init; }

    public long? FreeBytes { get; init; }

    public long? TotalBytes { get; init; }

    public bool? IsBitLockerProtected { get; init; }

    public bool? IsAutoUnlockEnabled { get; init; }

    public static VolumePayload From(HostVolume volume) => new()
    {
        Name = volume.Name,
        FreeBytes = volume.FreeBytes,
        TotalBytes = volume.TotalBytes,
        IsBitLockerProtected = volume.IsBitLockerProtected,
        IsAutoUnlockEnabled = volume.IsAutoUnlockEnabled,
    };

    public HostVolume ToVolume() =>
        new(
            this.Name ?? "",
            this.FreeBytes,
            this.TotalBytes,
            this.IsBitLockerProtected,
            this.IsAutoUnlockEnabled);
}

internal sealed record CertificatePayload
{
    public string? Thumbprint { get; init; }

    public string? CommonName { get; init; }

    public DateTimeOffset? NotAfter { get; init; }

    public static CertificatePayload? From(CertificateFact? certificate) =>
        certificate is null
            ? null
            : new CertificatePayload
            {
                Thumbprint = certificate.Thumbprint,
                CommonName = certificate.CommonName,
                NotAfter = certificate.NotAfter,
            };

    /// A certificate with no expiry date is not a certificate this rule can judge, so the
    /// whole fact is dropped rather than dated to the epoch.
    public CertificateFact? ToFact() =>
        this.NotAfter is { } notAfter
            ? new CertificateFact(this.Thumbprint ?? "", this.CommonName ?? "", notAfter)
            : null;
}
