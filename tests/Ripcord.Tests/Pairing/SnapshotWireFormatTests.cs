using Ripcord.Adapters.Pairing.Wire;
using Ripcord.Domain.Inventory;
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
        Assert.Contains("\"schema_version\":2", SnapshotWireFormat.Write(Populated()));
    }

    /// Milestone 2's rules are cross-host, so the facts they compare have to cross the wire —
    /// the listener never reads Hyper-V itself (decision D18).
    [Fact]
    public void The_host_and_vm_facts_survive_the_round_trip()
    {
        HostSnapshot original = Populated();

        HostSnapshot? read = SnapshotWireFormat.Read(SnapshotWireFormat.Write(original));

        Assert.Equal(original, read);
        Assert.Equal(12288, read!.State.Facts!.PhysicalRamMb);
        Assert.Equal("vSwitch-PROD", read.State.Vms[0].Facts!.Adapters[0].SwitchName);
    }

    /// A host still on the milestone 1b binary publishes schema 1. Its state is read, its
    /// facts are absent, and `check` reports the rules that needed them as unevaluable —
    /// refusing the payload outright would blind the pair view during a rolling update.
    [Fact]
    public void A_schema_one_payload_is_read_with_its_facts_absent()
    {
        string legacy = "{\"schema_version\":1,"
            + "\"captured_at\":\"2026-09-13T14:00:00+00:00\","
            + "\"host_name\":\"HV-PRIMARY-01\","
            + "\"vms\":[{\"name\":\"VM-DC-01\",\"role\":1,\"state\":3,\"health\":1,"
            + "\"last_replication_time\":\"2026-09-13T13:59:32+00:00\",\"pending_bytes\":4194304}]}";

        HostSnapshot? read = SnapshotWireFormat.Read(legacy);

        Assert.NotNull(read);
        Assert.Null(read.State.Facts);
        Assert.Null(read.State.Vms[0].Facts);
        Assert.Equal(ReplicationRole.Primary, read.State.Vms[0].Role);
    }

    /// The power state was added without moving the version, because the readable-versions
    /// gate protects whoever holds the list: raising it would make a host that has not been
    /// updated yet reject its peer's snapshot outright. A peer that predates the field simply
    /// omits it.
    ///
    /// It must arrive absent, never as Off. "Not reported" and "positively switched off" are
    /// opposite inputs to the split-brain reading, and a default here would let a silent peer
    /// stand in as half the evidence for a conflict that is not happening.
    [Fact]
    public void A_payload_without_a_power_state_yields_null_rather_than_off()
    {
        string withoutPower = "{\"schema_version\":2,"
            + "\"captured_at\":\"2026-09-13T14:00:00+00:00\","
            + "\"host_name\":\"HV-PRIMARY-01\","
            + "\"vms\":[{\"name\":\"VM-DC-01\",\"role\":1,\"state\":3,\"health\":1,"
            + "\"last_replication_time\":\"2026-09-13T13:59:32+00:00\",\"pending_bytes\":0}]}";

        HostSnapshot? read = SnapshotWireFormat.Read(withoutPower);

        Assert.NotNull(read);
        Assert.Null(read.State.Vms[0].PowerState);
    }

    /// A number this binary does not recognise is Unknown rather than whatever enum member
    /// happens to sit at that ordinal — and still not Off.
    [Fact]
    public void An_unrecognised_power_state_is_unknown_rather_than_a_neighbouring_value()
    {
        string odd = "{\"schema_version\":2,"
            + "\"captured_at\":\"2026-09-13T14:00:00+00:00\","
            + "\"host_name\":\"HV-PRIMARY-01\","
            + "\"vms\":[{\"name\":\"VM-DC-01\",\"role\":1,\"state\":3,\"health\":1,"
            + "\"pending_bytes\":0,\"power_state\":4242}]}";

        HostSnapshot? read = SnapshotWireFormat.Read(odd);

        Assert.Equal(VmPowerState.Unknown, read!.State.Vms[0].PowerState);
    }

    [Fact]
    public void A_power_state_survives_the_round_trip()
    {
        HostSnapshot original = new(
            Now,
            new HostState(
                "HV-PRIMARY-01",
                [
                    new VmReplicationState(
                        "VM-DC-01",
                        ReplicationRole.Primary,
                        ReplicationState.Replicating,
                        ReplicationHealth.Normal,
                        Now.AddSeconds(-20),
                        0,
                        null,
                        VmPowerState.Running),
                ],
                HostReachability.Reachable()));

        HostSnapshot? read = SnapshotWireFormat.Read(SnapshotWireFormat.Write(original));

        Assert.Equal(VmPowerState.Running, read!.State.Vms[0].PowerState);
    }

    [Fact]
    public void A_startup_action_survives_the_round_trip()
    {
        HostSnapshot original = new(
            Now,
            new HostState(
                "HV-PRIMARY-01",
                [
                    new VmReplicationState(
                        "VM-DC-01",
                        ReplicationRole.Primary,
                        ReplicationState.Replicating,
                        ReplicationHealth.Normal,
                        Now.AddSeconds(-20),
                        0,
                        null,
                        VmPowerState.Running,
                        AutomaticStartAction.StartIfRunning),
                ],
                HostReachability.Reachable()));

        HostSnapshot? read = SnapshotWireFormat.Read(SnapshotWireFormat.Write(original));

        Assert.Equal(AutomaticStartAction.StartIfRunning, read!.State.Vms[0].StartAction);
    }

    /// A peer that has not been updated sends nothing here, and its VMs must not read as
    /// fenced. "Not reported" and "will not boot" are opposite inputs to the fencing decision.
    [Fact]
    public void A_payload_without_a_startup_action_yields_null_rather_than_nothing()
    {
        string withoutAction = "{\"schema_version\":2,"
            + "\"captured_at\":\"2026-09-13T14:00:00+00:00\","
            + "\"host_name\":\"HV-PRIMARY-01\","
            + "\"vms\":[{\"name\":\"VM-DC-01\",\"role\":1,\"state\":3,\"health\":1,"
            + "\"pending_bytes\":0}]}";

        HostSnapshot? read = SnapshotWireFormat.Read(withoutAction);

        Assert.Null(read!.State.Vms[0].StartAction);
    }

    /// Null facts are the degraded shape: a host that could not read its own memory settings
    /// must publish the gap, not a zero that reads as a VM needing no RAM.
    [Fact]
    public void Absent_facts_survive_as_absent_rather_than_as_zero()
    {
        HostSnapshot snapshot = new(
            Now,
            new HostState(
                "HV-PRIMARY-01",
                [
                    new VmReplicationState(
                        "VM-DC-01",
                        ReplicationRole.Primary,
                        ReplicationState.Replicating,
                        ReplicationHealth.Normal,
                        Now,
                        0,
                        new VmFacts(null, null, null, [], [], null)),
                ],
                HostReachability.Reachable(),
                HostFacts.Unknown()));

        HostSnapshot? read = SnapshotWireFormat.Read(SnapshotWireFormat.Write(snapshot));

        Assert.Equal(snapshot, read);
        Assert.Null(read!.State.Vms[0].Facts!.StartupRamMb);
        Assert.False(read.State.Vms[0].Facts!.KnowsWhichDisksReplicate);
    }

    /// A peer running a version this one does not understand is refused rather than guessed
    /// at, exactly as an unknown config schema is.
    [Fact]
    public void A_payload_of_an_unknown_schema_version_is_refused()
    {
        string json = SnapshotWireFormat.Write(Populated())
            .Replace("\"schema_version\":2", "\"schema_version\":99", StringComparison.Ordinal);

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
    [InlineData("{\"schema_version\":2}")]
    [InlineData("{\"schema_version\":2,\"host_name\":null,\"captured_at\":null,\"vms\":null}")]
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
                        4_194_304,
                        new VmFacts(
                            2048,
                            4096,
                            1024,
                            [
                                new VirtualAdapter(
                                    "Network Adapter",
                                    "vSwitch-PROD",
                                    true,
                                    "00-15-5D-01-02-03",
                                    false,
                                    10),
                            ],
                            [new VmDisk(@"D:\VMs\dc-os.vhdx", false)],
                            [@"D:\VMs\dc-os.vhdx"])),
                    new VmReplicationState(
                        "VM-BACKUP-01",
                        ReplicationRole.None,
                        ReplicationState.Disabled,
                        ReplicationHealth.Unknown,
                        null,
                        null,
                        new VmFacts(
                            2048,
                            null,
                            null,
                            [new VirtualAdapter("Network Adapter", null, false, null, true, null)],
                            [new VmDisk(@"\\.\PHYSICALDRIVE2", true)],
                            null)),
                ],
                HostReachability.Reachable(),
                new HostFacts(
                    12288,
                    [new HostVolume("D:", 500_000_000_000, 2_000_000_000_000, true, true)],
                    new CertificateFact(
                        "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555",
                        "CN=HV-PRIMARY-01",
                        new DateTimeOffset(2029, 9, 1, 0, 0, 0, TimeSpan.Zero)))));
}
