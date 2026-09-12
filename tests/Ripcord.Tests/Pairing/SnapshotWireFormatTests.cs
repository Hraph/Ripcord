using Ripcord.Adapters.Pairing.Wire;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Pairing;

/// The only thing the listener does with network input is deserialise it, so this is the
/// attack surface. Fixed schema, no polymorphism, no type names in the payload, size-capped.
public class SnapshotWireFormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_snapshot_survives_the_round_trip()
    {
        HostSnapshot original = Populated();

        HostSnapshot? read = SnapshotWireFormat.Read(SnapshotWireFormat.Write(original));

        Assert.Equal(original, read);
    }

    /// The empty and degraded shapes are the ones a real incident produces, and the ones a
    /// round-trip is most likely to quietly alter.
    [Fact]
    public void A_host_with_no_vms_survives_the_round_trip()
    {
        HostSnapshot empty = new(
            Now, new HostState("HV-PRIMARY-01", [], HostReachability.Reachable()));

        Assert.Equal(empty, SnapshotWireFormat.Read(SnapshotWireFormat.Write(empty)));
    }

    [Fact]
    public void Null_lag_and_null_pending_survive_as_null_not_as_zero()
    {
        HostSnapshot snapshot = new(
            Now,
            new HostState(
                "HV-PRIMARY-01",
                [
                    new VmReplicationState(
                        "VM-BACKUP-01",
                        ReplicationRole.None,
                        ReplicationState.Disabled,
                        ReplicationHealth.Unknown,
                        null,
                        null),
                ],
                HostReachability.Reachable()));

        VmReplicationState vm = SnapshotWireFormat.Read(SnapshotWireFormat.Write(snapshot))!
            .State.Vms[0];

        Assert.Null(vm.LastReplicationTime);
        Assert.Null(vm.PendingBytes);
    }

    /// Enums cross the wire as their numeric value, never as a name a rename could break and
    /// never as a type the payload gets to choose.
    [Fact]
    public void The_payload_carries_no_type_names()
    {
        string json = SnapshotWireFormat.Write(Populated());

        Assert.DoesNotContain("$type", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ripcord.", json, StringComparison.Ordinal);
    }

    [Fact]
    public void The_payload_declares_its_schema_version()
    {
        Assert.Contains("\"schema_version\":1", SnapshotWireFormat.Write(Populated()));
    }

    /// A peer running a version this one does not understand is refused rather than guessed
    /// at, exactly as an unknown config schema is.
    [Fact]
    public void A_payload_of_an_unknown_schema_version_is_refused()
    {
        string json = SnapshotWireFormat.Write(Populated())
            .Replace("\"schema_version\":1", "\"schema_version\":99", StringComparison.Ordinal);

        Assert.Null(SnapshotWireFormat.Read(json));
    }

    /// Every malformed input ends in null, never in an exception reaching the caller: this
    /// runs in a network-facing service, and a throw there is the bug that matters.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"schema_version\":1}")]
    [InlineData("{\"schema_version\":1,\"host_name\":null,\"captured_at\":null,\"vms\":null}")]
    public void Malformed_input_yields_null_rather_than_an_exception(string payload)
    {
        Assert.Null(SnapshotWireFormat.Read(payload));
    }

    /// An unbounded payload is a denial of service against a service that reads before it
    /// parses. The cap is generous for three VMs and still finite.
    [Fact]
    public void An_oversized_payload_is_refused_before_it_is_parsed()
    {
        string oversized = new('x', SnapshotWireFormat.MaxPayloadBytes + 1);

        Assert.Null(SnapshotWireFormat.Read(oversized));
    }

    /// A real pair's snapshot has to fit comfortably inside the cap, or the cap is the bug.
    [Fact]
    public void A_realistic_snapshot_is_far_inside_the_cap()
    {
        Assert.True(SnapshotWireFormat.Write(Populated()).Length < SnapshotWireFormat.MaxPayloadBytes / 10);
    }

    /// An unreachable host is never serialised: the listener answers with what it knows about
    /// itself, and a host that cannot read its own Hyper-V does not publish a confident empty
    /// inventory.
    [Fact]
    public void A_snapshot_of_an_unreachable_host_is_refused_on_the_way_out()
    {
        HostSnapshot unreadable = new(
            Now,
            HostState.Unreachable("HV-PRIMARY-01", HostReachability.Failed("WMI down", Now)));

        Assert.Throws<ArgumentException>(() => SnapshotWireFormat.Write(unreadable));
    }

    private static HostSnapshot Populated() =>
        new(
            Now,
            new HostState(
                "HV-PRIMARY-01",
                [
                    new VmReplicationState(
                        "VM-DC-01",
                        ReplicationRole.Primary,
                        ReplicationState.Replicating,
                        ReplicationHealth.Normal,
                        Now.AddSeconds(-28),
                        4_194_304),
                    new VmReplicationState(
                        "VM-BACKUP-01",
                        ReplicationRole.None,
                        ReplicationState.Disabled,
                        ReplicationHealth.Unknown,
                        null,
                        null),
                ],
                HostReachability.Reachable()));
}
