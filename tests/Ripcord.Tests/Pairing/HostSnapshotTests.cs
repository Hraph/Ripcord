using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Pairing;

/// The listener never talks to Hyper-V (decision D18), so the peer view is as old as the last
/// snapshot. Its age is therefore part of the answer, not a detail: on the day of an incident,
/// knowing the peer view is four minutes old is information.
public class HostSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_snapshot_knows_how_old_it_is()
    {
        HostSnapshot snapshot = Snapshot(Now.AddMinutes(-4));

        Assert.Equal(TimeSpan.FromMinutes(4), snapshot.AgeAt(Now));
    }

    /// The two hosts' clocks drift; a snapshot stamped in the future is skew, not a negative
    /// age. Same rule as every other elapsed time in Ripcord.
    [Fact]
    public void A_snapshot_from_the_future_is_clock_skew_not_a_negative_age()
    {
        Assert.Equal(TimeSpan.Zero, Snapshot(Now.AddMinutes(2)).AgeAt(Now));
    }

    /// Fresh enough to act on is a threshold, and the operator already configured one: the
    /// same `peer.offline_after_sec` that decides when silence becomes an outage.
    [Fact]
    public void A_snapshot_within_the_threshold_is_fresh()
    {
        Assert.True(Snapshot(Now.AddSeconds(-119)).IsFreshAt(Now, TimeSpan.FromSeconds(120)));
    }

    [Fact]
    public void A_snapshot_older_than_the_threshold_is_stale()
    {
        Assert.False(Snapshot(Now.AddSeconds(-120)).IsFreshAt(Now, TimeSpan.FromSeconds(120)));
    }

    /// A stale snapshot is still shown — it is the best information available, and hiding it
    /// would leave the operator with nothing. It is shown as stale.
    [Fact]
    public void A_stale_snapshot_still_carries_its_host_state()
    {
        HostSnapshot snapshot = Snapshot(Now.AddHours(-9));

        Assert.False(snapshot.IsFreshAt(Now, TimeSpan.FromSeconds(120)));
        Assert.Equal("HV-PRIMARY-01", snapshot.State.HostName);
    }

    private static HostSnapshot Snapshot(DateTimeOffset capturedAt) =>
        new(
            capturedAt,
            new HostState(
                "HV-PRIMARY-01",
                [
                    new VmReplicationState(
                        "VM-DC-01",
                        ReplicationRole.Primary,
                        ReplicationState.Replicating,
                        ReplicationHealth.Normal,
                        capturedAt.AddSeconds(-20),
                        1024),
                ],
                HostReachability.Reachable()));
}
