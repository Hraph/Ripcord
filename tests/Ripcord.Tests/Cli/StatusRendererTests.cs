using Ripcord.Cli.Rendering;
using Ripcord.Cli;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Deployment;
using Ripcord.Domain;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Cli;

/// The rendering is read off a 1024×768 KVM at 3 a.m. Fixed columns, no dependency on
/// terminal width, no colour carrying anything on its own.
public class StatusRendererTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 14, 0, 0, TimeSpan.Zero);

    /// Golden output, because the layout *is* the deliverable: a column that silently moves
    /// is a regression even though nothing throws.
    [Fact]
    public void The_degraded_pair_renders_exactly_as_specified()
    {
        string expected = """
            RIPCORD STATUS                                      2026-09-12 14:00:00 UTC
            {version}

            LOCAL   HV-REPLICA-01                                             REACHABLE
              VM                   ROLE     STATE            HEALTH       LAG   PENDING
              -------------------------------------------------------------------------
              VM-BACKUP-01         None     Disabled         Unknown        -         -
              VM-DC-01             Replica  Resynchronizing  Critical   6h00m     16 GB
              VM-LEGACY-01         Replica  Replicating      Warning    9m00s    256 MB

            PEER    HV-PRIMARY-01                                               OFFLINE
              No peer channel configured on this node.
              Unreachable since: never contacted

            """;

        Assert.Equal(
            expected.Replace("{version}", $"ripcord {BuildInfo.VersionWithCommit}", StringComparison.Ordinal),
            Render(DegradedPair()));
    }

    /// Last on the page, after both hosts: it explains what the other host shows about this one.
    [Fact]
    public void A_listener_alert_closes_the_page()
    {
        ListenerAlert alert = new(
            "NOT RUNNING",
            "the listener service is stopped, so HV-PRIMARY-01 cannot read this host",
            "ripcord service",
            Critical: true);

        string rendered = StatusRenderer.Render(
            DegradedPair(), TimeSpan.FromSeconds(120), Now, listener: alert);

        Assert.EndsWith(
            """
            LISTENER  NOT RUNNING
              The listener service is stopped, so HV-PRIMARY-01 cannot read this host.
              Next: ripcord service

            """,
            rendered,
            StringComparison.Ordinal);
        Assert.All(
            rendered.Split('\n'),
            line => Assert.True(line.Length <= StatusRenderer.Width, line));
    }

    /// A reason carried from Windows can end with its own full stop and line break.
    [Fact]
    public void A_listener_reason_that_ends_a_sentence_is_not_doubled()
    {
        ListenerAlert alert = new(
            "UNKNOWN", "Windows did not say whether it runs: The RPC server is unavailable.\r\n",
            "ripcord service", Critical: false);

        string rendered = StatusRenderer.Render(
            DegradedPair(), TimeSpan.FromSeconds(120), Now, listener: alert);

        Assert.Contains(
            "  Windows did not say whether it runs: The RPC server is unavailable.\n  Next:",
            rendered.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    /// The peer answers with a snapshot, never live. Saying how old it is beats the illusion
    /// of live data — a known age is information, not a defect.
    [Fact]
    public void The_peer_section_states_how_old_its_snapshot_is()
    {
        string rendered = Render(PairWithPeerSnapshot(Now.AddSeconds(-90)));

        Assert.Contains("State as of", rendered, StringComparison.Ordinal);
        Assert.Contains("1m30s old", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("STALE", rendered, StringComparison.Ordinal);
    }

    /// Past `peer.offline_after_sec` the operator is told in a word, rather than left to
    /// subtract two timestamps at 3 a.m.
    [Fact]
    public void A_snapshot_older_than_the_threshold_is_marked_stale()
    {
        Assert.Contains(
            "STALE", Render(PairWithPeerSnapshot(Now.AddMinutes(-30))), StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_section_never_claims_a_snapshot_age()
    {
        string local = Render(PairWithPeerSnapshot(Now.AddMinutes(-4)))
            .Split("PEER", StringSplitOptions.None)[0];

        Assert.DoesNotContain("State as of", local, StringComparison.Ordinal);
    }

    private static PairView PairWithPeerSnapshot(DateTimeOffset capturedAt) =>
        new(
            new HostState("HV-REPLICA-01", [], HostReachability.Reachable()),
            new HostState(
                "HV-PRIMARY-01",
                [Vm("VM-DC-01", ReplicationHealth.Normal, capturedAt.AddSeconds(-10))],
                HostReachability.Reachable()),
            capturedAt);

    /// The skew that refuses a failover, visible before it refuses one. Until now the only way
    /// to discover the pair was on two builds was to be stopped by it, mid-command.
    [Fact]
    public void Two_different_builds_are_named_on_the_page_that_describes_the_pair()
    {
        string rendered = Render(
            PairWithPeerSnapshot(Now) with { PeerBuild = new BuildIdentity("0.9.9", "abcdef123456") });

        Assert.Contains("0.9.9+abcdef123456 on the peer", rendered, StringComparison.Ordinal);
        Assert.Contains("refused", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_build_on_both_hosts_is_said_once()
    {
        string rendered = Render(
            PairWithPeerSnapshot(Now) with
            {
                PeerBuild = new BuildIdentity(BuildInfo.Version, BuildInfo.CommitHash),
            });

        Assert.Contains("on both hosts", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", rendered, StringComparison.Ordinal);
    }

    /// A peer that published nothing says nothing. "Cannot be compared" is not "the same".
    [Fact]
    public void A_peer_that_published_no_build_adds_no_claim()
    {
        string rendered = Render(PairWithPeerSnapshot(Now));

        Assert.DoesNotContain("on the peer", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("on both hosts", rendered, StringComparison.Ordinal);
    }

    /// The layout is 75 columns and one line ending, on Windows as in the Linux container.
    /// A carriage return the framework added is a 76th column and a byte the fixture tests
    /// cannot see — which is exactly how it reached a release build unnoticed.
    [Theory]
    [MemberData(nameof(EveryFixture))]
    public void Nothing_rendered_carries_the_hosts_own_line_ending(PairView view) =>
        Assert.DoesNotContain('\r', Render(view));

    /// No line may exceed the fixed width, whatever the data — that is what makes the output
    /// readable without a terminal wide enough to be generous.
    [Theory]
    [MemberData(nameof(EveryFixture))]
    public void No_line_exceeds_the_fixed_width(PairView view)
    {
        string rendered = Render(view);

        Assert.All(
            rendered.Split('\n'),
            line => Assert.True(
                line.Length <= StatusRenderer.Width,
                $"line of {line.Length} chars exceeds {StatusRenderer.Width}: {line}"));
    }

    /// The fixtures only exercise the states they happen to use. A state added later whose
    /// default name is long would push the PENDING column sideways with every test still
    /// green, so every state is rendered here.
    [Theory]
    [MemberData(nameof(EveryReplicationState))]
    public void Every_replication_state_fits_its_column(ReplicationState state)
    {
        PairView view = Pair(
            new HostState(
                "HV-REPLICA-01",
                [
                    new VmReplicationState(
                        "VM-DC-01",
                        ReplicationRole.Replica,
                        state,
                        ReplicationHealth.Critical,
                        Now.AddMinutes(-1),
                        1024),
                ],
                HostReachability.Reachable()));

        Assert.Equal(StatusRenderer.Width, RowFor(Render(view), "VM-DC-01").Length);
    }

    public static TheoryData<ReplicationState> EveryReplicationState =>
        new(Enum.GetValues<ReplicationState>());

    /// A VM name longer than its column must not push every later column sideways.
    [Fact]
    public void An_over_long_vm_name_is_truncated_rather_than_widening_the_table()
    {
        PairView view = Pair(
            new HostState(
                "HV-REPLICA-01",
                [Vm("VM-WITH-AN-EXTREMELY-LONG-NAME", ReplicationHealth.Normal, Now)],
                HostReachability.Reachable()));

        string row = RowFor(Render(view), "VM-WITH");

        Assert.Contains("VM-WITH-AN-EXTREM...", row);
        Assert.True(row.Length <= StatusRenderer.Width);
    }

    /// Never replicated is not a lag of zero, and no pending volume is not zero bytes.
    [Fact]
    public void Unknown_values_render_as_a_dash_never_as_zero()
    {
        PairView view = Pair(
            new HostState(
                "HV-REPLICA-01",
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

        string row = RowFor(Render(view), "VM-BACKUP-01");

        Assert.DoesNotContain("0s", row);
        Assert.DoesNotContain(" 0 B", row);
        Assert.EndsWith("-         -", row);
    }

    /// A host with no VM at all is a real state — a fresh DR host — and must read as such
    /// rather than as an empty table the operator mistakes for a failure.
    [Fact]
    public void A_reachable_host_with_no_vms_says_so()
    {
        PairView view = Pair(
            new HostState("HV-REPLICA-01", [], HostReachability.Reachable()));

        Assert.Contains("no virtual machine", Render(view), StringComparison.OrdinalIgnoreCase);
    }

    /// A timeout means a host that may be dead; a refusal means a listener that is not
    /// running. Collapsing them loses the only clue about what to do next.
    [Theory]
    [InlineData(ReachabilityKind.TimedOut, "no answer before the timeout")]
    [InlineData(ReachabilityKind.Refused, "connection refused")]
    public void Timeout_and_refusal_read_differently(ReachabilityKind kind, string expected)
    {
        HostReachability reachability = kind == ReachabilityKind.TimedOut
            ? HostReachability.TimedOut(Now.AddMinutes(-5))
            : HostReachability.Refused(Now.AddMinutes(-5));

        string rendered = Render(Pair(
            new HostState("HV-REPLICA-01", [], HostReachability.Reachable()),
            HostState.Unreachable("HV-PRIMARY-01", reachability)));

        Assert.Contains(expected, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5m00s ago", rendered);
    }

    /// Below `peer.offline_after_sec` the peer is silent, not offline — the word the operator
    /// reads has to match the threshold they configured.
    [Fact]
    public void A_peer_silent_for_less_than_the_threshold_does_not_read_as_offline()
    {
        string rendered = Render(Pair(
            new HostState("HV-REPLICA-01", [], HostReachability.Reachable()),
            HostState.Unreachable(
                "HV-PRIMARY-01", HostReachability.TimedOut(Now.AddSeconds(-30)))));

        Assert.Contains("SILENT", rendered);
        Assert.DoesNotContain("OFFLINE", rendered);
    }

    /// Values no friendly fixture produces: replication broken for months, a terabyte
    /// pending, and host and VM names past their columns.
    public static TheoryData<PairView> EveryFixture => new(
        DegradedPair(),
        Pair(
            new HostState(
                new string('H', 60),
                [
                    new VmReplicationState(
                        new string('V', 60),
                        ReplicationRole.Replica,
                        ReplicationState.WaitingToCompleteInitialReplication,
                        ReplicationHealth.Critical,
                        Now.AddDays(-400),
                        3_298_534_883_328),
                ],
                HostReachability.Reachable()),
            HostState.Unreachable(
                new string('P', 60), HostReachability.Refused(Now.AddDays(-400)))));

    /// A lag past 99 days must not push the PENDING column sideways. A VM whose replication
    /// broke months ago is exactly the case that produces it.
    [Fact]
    public void A_lag_beyond_the_column_is_capped_rather_than_overflowing()
    {
        PairView view = Pair(
            new HostState(
                "HV-REPLICA-01",
                [Vm("VM-DC-01", ReplicationHealth.Critical, Now.AddDays(-400))],
                HostReachability.Reachable()));

        Assert.Contains(">99d", RowFor(Render(view), "VM-DC-01"));
    }

    /// The sequences are encoded in the binary and span two hosts, so version skew between
    /// the pair has to be visible on the page the operator is already reading.
    [Fact]
    public void The_banner_carries_the_version_and_commit_hash()
    {
        Assert.Contains(BuildInfo.VersionWithCommit, Render(DegradedPair()));
    }

    /// VMs are ordered here, not by whichever adapter happened to enumerate them — the fake
    /// and WMI must render the same page from the same set.
    [Fact]
    public void Vms_are_ordered_by_name_whatever_order_the_provider_returned_them_in()
    {
        PairView view = Pair(
            new HostState(
                "HV-REPLICA-01",
                [
                    Vm("VM-LEGACY-01", ReplicationHealth.Normal, Now),
                    Vm("vm-backup-01", ReplicationHealth.Normal, Now),
                    Vm("VM-DC-01", ReplicationHealth.Normal, Now),
                ],
                HostReachability.Reachable()));

        string[] names = Render(view).Split('\n')
            .Where(line => line.Contains("VM-", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("ROLE", StringComparison.Ordinal))
            .Select(line => line.Trim().Split(' ')[0])
            .ToArray();

        Assert.Equal(["vm-backup-01", "VM-DC-01", "VM-LEGACY-01"], names);
    }

    /// A running listener is one line, its build at the right edge, like a host's presence.
    [Fact]
    public void A_running_listener_is_one_line_with_its_build()
    {
        string rendered = StatusRenderer.Render(
            DegradedPair(), TimeSpan.FromSeconds(120), Now,
            running: new ListenerRunning(false, "0.7.0+def5678", false));
        string[] lines = rendered.Split('\n');

        string line = Assert.Single(lines, line => line.StartsWith("LISTENER", StringComparison.Ordinal));
        Assert.StartsWith("LISTENER  running", line, StringComparison.Ordinal);
        Assert.EndsWith("0.7.0+def5678", line, StringComparison.Ordinal);
        Assert.Equal(StatusRenderer.Width, line.Length);
        Assert.DoesNotContain("Next:", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_listener_on_another_build_than_this_binary_points_to_the_restart()
    {
        string rendered = StatusRenderer.Render(
            DegradedPair(), TimeSpan.FromSeconds(120), Now,
            running: new ListenerRunning(false, "0.6.0+abc1234", true));

        Assert.EndsWith(
            "  Not the build of this ripcord.exe. Next: ripcord service restart\n",
            rendered.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.All(rendered.Split('\n'), line => Assert.True(line.Length <= StatusRenderer.Width, line));
    }

    /// A problem is the block, never the line: the two cannot both be on the page.
    [Fact]
    public void An_alert_takes_the_place_of_the_running_line()
    {
        string rendered = StatusRenderer.Render(
            DegradedPair(), TimeSpan.FromSeconds(120), Now,
            listener: new ListenerAlert("NOT RUNNING", "stopped", "ripcord service", Critical: true),
            running: new ListenerRunning(false, "0.7.0+def5678", false));

        string line = Assert.Single(
            rendered.Split('\n'), line => line.StartsWith("LISTENER", StringComparison.Ordinal));
        Assert.Equal("LISTENER  NOT RUNNING", line);
    }

    /// The build comes from a file the listener wrote; however long, the line holds 75 columns.
    [Fact]
    public void A_starting_listener_with_an_overlong_build_stays_within_the_width()
    {
        string rendered = StatusRenderer.Render(
            DegradedPair(), TimeSpan.FromSeconds(120), Now,
            running: new ListenerRunning(true, new string('9', 120), false));

        string line = Assert.Single(
            rendered.Split('\n'), line => line.StartsWith("LISTENER", StringComparison.Ordinal));
        Assert.StartsWith("LISTENER  starting ", line, StringComparison.Ordinal);
        Assert.Equal(StatusRenderer.Width, line.Length);
    }

    private static string Render(PairView view) =>
        StatusRenderer.Render(view, TimeSpan.FromSeconds(120), Now);

    private static string RowFor(string rendered, string vmName) =>
        rendered.Split('\n').Single(line => line.Contains(vmName, StringComparison.Ordinal));

    private static PairView DegradedPair() =>
        Pair(
            new HostState(
                "HV-REPLICA-01",
                [
                    new VmReplicationState(
                        "VM-DC-01",
                        ReplicationRole.Replica,
                        ReplicationState.Resynchronizing,
                        ReplicationHealth.Critical,
                        Now.AddHours(-6),
                        17_179_869_184),
                    Vm("VM-LEGACY-01", ReplicationHealth.Warning, Now.AddMinutes(-9), 268_435_456),
                    new VmReplicationState(
                        "VM-BACKUP-01",
                        ReplicationRole.None,
                        ReplicationState.Disabled,
                        ReplicationHealth.Unknown,
                        null,
                        null),
                ],
                HostReachability.Reachable()));

    private static PairView Pair(HostState local, HostState? peer = null) =>
        new(
            local,
            peer ?? HostState.Unreachable(
                "HV-PRIMARY-01", HostReachability.NotConfigured()));

    private static VmReplicationState Vm(
        string name,
        ReplicationHealth health,
        DateTimeOffset lastReplication,
        long pendingBytes = 0) =>
        new(
            name,
            ReplicationRole.Replica,
            ReplicationState.Replicating,
            health,
            lastReplication,
            pendingBytes);
}
