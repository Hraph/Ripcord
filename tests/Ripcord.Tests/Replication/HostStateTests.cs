using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Replication;

/// The degraded path is the one milestone 1 must get right: it is what the operator sees when
/// the primary is actually down.
public class HostStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(120);

    [Fact]
    public void An_unreachable_host_reports_no_vms_rather_than_an_empty_inventory()
    {
        HostState peer = HostState.Unreachable(
            "HV-PRIMARY-01", HostReachability.TimedOut(Now.AddMinutes(-1)));

        Assert.False(peer.IsReachable);
        Assert.Empty(peer.Vms);
    }

    /// Rendering must tell the two apart: a refused connection is a listener that is not
    /// running, a timeout is a host that may be dead, and they call for different actions.
    [Fact]
    public void Timeout_and_refusal_are_distinct_reachability_kinds()
    {
        Assert.NotEqual(
            HostReachability.TimedOut(Now).Kind,
            HostReachability.Refused(Now).Kind);
    }

    [Fact]
    public void A_peer_that_answers_is_present()
    {
        Assert.Equal(
            PeerPresence.Reachable,
            HostReachability.Reachable().Presence(OfflineAfter, Now));
    }

    /// Below the threshold the peer is silent, not offline. Milestone 4 turns "offline" into a
    /// decision, so the two must not be conflated here either.
    [Fact]
    public void A_peer_silent_for_less_than_the_threshold_is_not_yet_offline()
    {
        HostReachability silent = HostReachability.TimedOut(Now.AddSeconds(-119));

        Assert.Equal(PeerPresence.Silent, silent.Presence(OfflineAfter, Now));
    }

    [Fact]
    public void A_peer_silent_for_the_threshold_is_offline()
    {
        HostReachability offline = HostReachability.TimedOut(Now.AddSeconds(-120));

        Assert.Equal(PeerPresence.Offline, offline.Presence(OfflineAfter, Now));
    }

    /// Milestone 1 has no peer transport at all, so "since when" has no answer. Treating an
    /// unknown start as offline is the honest reading: nothing has ever been heard.
    [Fact]
    public void A_peer_that_was_never_contacted_is_offline_with_no_start_time()
    {
        HostReachability never = HostReachability.NotConfigured();

        Assert.Null(never.UnreachableSince);
        Assert.Equal(PeerPresence.Offline, never.Presence(OfflineAfter, Now));
        Assert.Null(never.UnreachableFor(Now));
    }

    [Fact]
    public void Unreachable_duration_is_measured_from_the_recorded_start()
    {
        HostReachability refused = HostReachability.Refused(Now.AddMinutes(-7));

        Assert.Equal(TimeSpan.FromMinutes(7), refused.UnreachableFor(Now));
    }

    [Fact]
    public void A_reachable_host_has_no_unreachable_duration()
    {
        Assert.Null(HostReachability.Reachable().UnreachableFor(Now));
    }
}
