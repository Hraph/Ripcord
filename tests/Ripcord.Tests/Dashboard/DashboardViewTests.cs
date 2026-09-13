using Ripcord.Domain.Checks;
using Ripcord.Domain.Dashboard;
using Ripcord.Domain.Replication;
using Ripcord.Tests.Checks;

namespace Ripcord.Tests.Dashboard;

/// The page is read by somebody who did not type a command and is not watching a terminal.
/// Every test here is about what it says when something is missing, because that is the
/// state the page will be in on the day it matters.
public sealed class DashboardViewTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(2);

    [Fact]
    public void A_pair_with_nothing_wrong_reads_as_ready()
    {
        DashboardView page = Page(Pairs.Healthy(Now), Report());

        Assert.Equal(DashboardVerdict.Ready, page.Verdict);
        Assert.Contains("no critical finding", page.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_critical_finding_makes_the_page_read_as_broken()
    {
        DashboardView page = Page(Pairs.Healthy(Now), Report(Critical()));

        Assert.Equal(DashboardVerdict.Broken, page.Verdict);
        Assert.Contains("1 critical", page.Headline, StringComparison.OrdinalIgnoreCase);
    }

    /// The page refreshes itself, so an unreadable local host has to say so on the page.
    /// Anything else leaves yesterday's reading on screen with nothing marking it as old.
    [Fact]
    public void An_unreadable_local_host_is_never_ready()
    {
        PairView view = Pairs.Healthy(Now) with
        {
            Local = HostState.Unreachable(
                Pairs.Target, HostReachability.Failed("WMI did not answer", Now.AddMinutes(-1))),
        };

        DashboardView page = Page(view, report: null);

        Assert.Equal(DashboardVerdict.Unknown, page.Verdict);
        Assert.Contains("could not be read", page.Headline, StringComparison.OrdinalIgnoreCase);
    }

    /// A critical outranks every other reason the page might be uncertain. Softening it into
    /// "unknown" because something else was also unreadable is how a page stops being read.
    [Fact]
    public void A_critical_finding_outranks_an_unreadable_local_host()
    {
        PairView view = Pairs.Healthy(Now) with
        {
            Local = HostState.Unreachable(
                Pairs.Target, HostReachability.Failed("WMI did not answer", Now.AddMinutes(-1))),
        };

        DashboardView page = Page(view, Report(Critical()));

        Assert.Equal(DashboardVerdict.Broken, page.Verdict);
    }

    [Fact]
    public void A_silent_peer_degrades_the_page_rather_than_breaking_it()
    {
        PairView view = Pairs.Healthy(Now).WithUnreachableSource(Now.AddSeconds(-30));

        DashboardView page = Page(view, Report());

        Assert.Equal(DashboardVerdict.Degraded, page.Verdict);
        Assert.Contains("peer", page.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_rule_that_could_not_be_checked_degrades_the_page()
    {
        DashboardView page = Page(Pairs.Healthy(Now), Report(Unevaluable()));

        Assert.Equal(DashboardVerdict.Degraded, page.Verdict);
        Assert.Contains("could not be checked", page.Headline, StringComparison.OrdinalIgnoreCase);
    }

    /// No check at all is not a clean bill of health.
    [Fact]
    public void A_page_built_without_a_check_report_is_never_ready()
    {
        DashboardView page = Page(Pairs.Healthy(Now), report: null);

        Assert.Equal(DashboardVerdict.Degraded, page.Verdict);
    }

    [Fact]
    public void A_warning_alone_does_not_break_the_page()
    {
        DashboardView page = Page(Pairs.Healthy(Now), Report(Warning()));

        Assert.Equal(DashboardVerdict.Degraded, page.Verdict);
        Assert.Single(page.Warnings);
        Assert.Empty(page.Criticals);
    }

    [Fact]
    public void Findings_are_split_by_what_the_reader_has_to_act_on_first()
    {
        DashboardView page = Page(
            Pairs.Healthy(Now), Report(Critical(), Warning(), Unevaluable()));

        Assert.Single(page.Criticals);
        Assert.Single(page.Warnings);
        Assert.Single(page.Unevaluated);
    }

    [Fact]
    public void Each_panel_carries_the_machines_of_its_own_host()
    {
        DashboardView page = Page(Pairs.Healthy(Now), Report());

        Assert.Equal(Pairs.Target, page.Local!.HostName);
        Assert.Equal(Pairs.Source, page.Peer!.HostName);
        Assert.Equal(Pairs.Names.Count, page.Local.Vms.Count);
        Assert.Equal(PeerPresence.Reachable, page.Local.Presence);
    }

    /// The peer answers with a snapshot, never live. A page that showed it without its age
    /// would be presenting a four-hour-old reading as the current state of the pair.
    [Fact]
    public void A_peer_snapshot_older_than_the_offline_threshold_is_marked_stale()
    {
        PairView view = Pairs.Healthy(Now) with { PeerCapturedAt = Now.AddMinutes(-10) };

        DashboardView page = Page(view, Report());

        Assert.True(page.Peer!.IsStale);
        Assert.Equal(TimeSpan.FromMinutes(10), page.Peer.Age);
        Assert.Equal(DashboardVerdict.Degraded, page.Verdict);
    }

    [Fact]
    public void A_fresh_peer_snapshot_is_not_stale()
    {
        PairView view = Pairs.Healthy(Now) with { PeerCapturedAt = Now.AddSeconds(-20) };

        DashboardView page = Page(view, Report());

        Assert.False(page.Peer!.IsStale);
        Assert.Equal(DashboardVerdict.Ready, page.Verdict);
    }

    [Fact]
    public void An_unreachable_host_says_why_on_its_own_panel()
    {
        PairView view = Pairs.Healthy(Now).WithUnreachableSource(Now.AddMinutes(-30));

        DashboardView page = Page(view, Report());

        Assert.Equal(PeerPresence.Offline, page.Peer!.Presence);
        Assert.Contains("timeout", page.Peer.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(page.Peer.Vms);
    }

    /// A page that cannot be built has to say what stopped it. A blank one, or one still
    /// showing the previous reading, is the failure this tool exists to avoid.
    [Fact]
    public void A_page_that_could_not_be_built_says_so_instead_of_rendering_nothing()
    {
        DashboardView page = DashboardView.Unavailable(Now, "ripcord.yaml is not readable");

        Assert.Equal(DashboardVerdict.Unknown, page.Verdict);
        Assert.Contains("ripcord.yaml is not readable", page.Headline, StringComparison.Ordinal);
        Assert.Null(page.Local);
        Assert.Null(page.Peer);
        Assert.Equal(Now, page.At);
    }

    [Fact]
    public void Notes_from_the_reading_are_carried_onto_the_page()
    {
        DashboardView page = DashboardView.Of(
            Pairs.Healthy(Now), Report(), OfflineAfter, Now, ["free space could not be read"]);

        Assert.Contains("free space could not be read", page.Notes);
    }

    private static DashboardView Page(PairView view, CheckReport? report) =>
        DashboardView.Of(view, report, OfflineAfter, Now, []);

    private static CheckReport Report(params Finding[] findings) =>
        new(Now, OperatingMode.Normal, Pairs.Source, Pairs.Target, true, findings, Feasibility(), []);

    private static Feasibility Feasibility() =>
        Ripcord.Domain.Checks.Feasibility.Calculate(
            [], Pairs.TargetHost(Now), 4);

    private static Finding Critical() =>
        new(
            CheckRules.ById(CheckRules.ReplicaSwitchMismatch)!,
            "VM-DC-01",
            "attached to vSwitch-OLD",
            "this VM would boot with no network on the target",
            null,
            FindingVerdict.Violated);

    private static Finding Warning() =>
        new(
            CheckRules.ById(CheckRules.CertificateNearExpiry)!,
            null,
            "expires in 20 days",
            "replication stops when it lapses",
            null,
            FindingVerdict.Violated);

    private static Finding Unevaluable() =>
        new(
            CheckRules.ById(CheckRules.ReplicaSwitchMismatch)!,
            "VM-LEGACY-01",
            "the target's copy could not be read",
            "this rule could not be checked",
            null,
            FindingVerdict.Unevaluable);
}
