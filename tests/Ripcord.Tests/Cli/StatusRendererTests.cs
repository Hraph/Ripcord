using Ripcord.Cli.Rendering;
using Ripcord.Cli;
using Ripcord.Domain.Configuration;
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
