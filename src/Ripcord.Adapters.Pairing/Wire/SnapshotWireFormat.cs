using System.Text.Json;
using System.Text.Json.Serialization;
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

    private const int SchemaVersion = 1;

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
    public int SchemaVersion { get; init; }

    public DateTimeOffset? CapturedAt { get; init; }

    public string? HostName { get; init; }

    public List<VmPayload>? Vms { get; init; }

    public static SnapshotPayload From(HostSnapshot snapshot) => new()
    {
        SchemaVersion = 1,
        CapturedAt = snapshot.CapturedAt,
        HostName = snapshot.State.HostName,
        Vms = [.. snapshot.State.Vms.Select(VmPayload.From)],
    };

    /// Anything missing, unknown or out of range yields null rather than a partly built
    /// snapshot: a peer view assembled from half a payload is worse than no peer view.
    public HostSnapshot? ToSnapshot()
    {
        if (this.SchemaVersion != 1
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
            new HostState(this.HostName, vms, HostReachability.Reachable()));
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

    public static VmPayload From(VmReplicationState vm) => new()
    {
        Name = vm.Name,
        Role = (int)vm.Role,
        State = (int)vm.State,
        Health = (int)vm.Health,
        LastReplicationTime = vm.LastReplicationTime,
        PendingBytes = vm.PendingBytes,
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
                this.PendingBytes);

    private static T Defined<T>(int value, T fallback)
        where T : struct, Enum =>
        Enum.IsDefined((T)(object)value) ? (T)(object)value : fallback;
}
