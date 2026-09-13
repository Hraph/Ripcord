using Ripcord.Cli.Rendering;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Dashboard;
using Ripcord.Domain.Replication;
using Ripcord.Tests.Checks;

namespace Ripcord.Tests.Dashboard;

/// One self-contained document, no script, no request to anywhere. The page is served on a
/// Hyper-V host during an incident: it has to render with the network down, and it must not
/// be able to execute anything a VM name happened to contain.
public sealed class DashboardRendererTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(30);

    [Fact]
    public void The_page_is_a_complete_html_document()
    {
        string html = Render(Healthy());

        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("<title>", html, StringComparison.Ordinal);
        Assert.EndsWith("</html>\n", html, StringComparison.Ordinal);
    }

    /// Nobody is going to press reload. The interval is the configured one, and it is in the
    /// document rather than in a script, so it still works with scripting switched off.
    [Fact]
    public void The_page_reloads_itself_at_the_configured_interval()
    {
        string html = DashboardRenderer.Render(Healthy(), TimeSpan.FromSeconds(45));

        Assert.Contains("<meta http-equiv=\"refresh\" content=\"45\">", html, StringComparison.Ordinal);
    }

    /// The host has no outbound access by design, and a page that fetched a stylesheet would
    /// render unstyled exactly when it is needed.
    [Theory]
    [InlineData("http://")]
    [InlineData("https://")]
    [InlineData("<script")]
    [InlineData("<img")]
    [InlineData("onclick")]
    public void The_page_reaches_for_nothing_and_runs_nothing(string forbidden)
    {
        string html = Render(Healthy());

        Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
    }

    /// VM and host names come off the hypervisor. They are data, and they are rendered as
    /// data — a name is never markup.
    [Fact]
    public void Names_read_off_the_hypervisor_are_escaped()
    {
        PairView view = Pairs.Healthy(Now).WithTargetVm(
            "VM-DC-01", vm => vm with { Name = "<script>alert(1)</script>" });

        string html = Render(Page(view));

        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_verdict_is_spelled_out_rather_than_only_coloured()
    {
        Assert.Contains("READY", Render(Healthy()), StringComparison.Ordinal);
        Assert.Contains("NOT READY", Render(Broken()), StringComparison.Ordinal);
    }

    [Fact]
    public void The_headline_is_on_the_page()
    {
        DashboardView page = Healthy();

        Assert.Contains(page.Headline, Render(page), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_machine_of_a_reachable_host_has_a_row()
    {
        string html = Render(Healthy());

        foreach (string name in Pairs.Names)
        {
            Assert.Contains(name, html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void An_unreachable_host_says_why_and_shows_no_table()
    {
        PairView view = Pairs.Healthy(Now).WithUnreachableSource(Now.AddMinutes(-30));

        string html = Render(Page(view));

        Assert.Contains("OFFLINE", html, StringComparison.Ordinal);
        Assert.Contains("no answer before the timeout", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_critical_finding_is_rendered_above_the_rules_that_could_not_be_checked()
    {
        string html = Render(Page(
            Pairs.Healthy(Now),
            Report(Critical(), Unevaluable())));

        int critical = html.IndexOf("CRITICAL", StringComparison.Ordinal);
        int notChecked = html.IndexOf("NOT CHECKED", StringComparison.Ordinal);

        Assert.True(critical >= 0 && notChecked > critical);
    }

    /// What was observed, what it means on the day, and what fixes it — the same three lines
    /// the console prints, because the page must not be a lesser view of the same check.
    [Fact]
    public void A_finding_carries_its_implication_and_its_remedy()
    {
        string html = Render(Page(Pairs.Healthy(Now), Report(Critical())));

        Assert.Contains("attached to vSwitch-OLD", html, StringComparison.Ordinal);
        Assert.Contains("would boot with no network", html, StringComparison.Ordinal);
        Assert.Contains("Connect-VMNetworkAdapter", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_page_that_could_not_be_built_still_renders_a_document_saying_why()
    {
        string html = DashboardRenderer.Render(
            DashboardView.Unavailable(Now, "ripcord.yaml is not readable"), Refresh);

        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        Assert.Contains("ripcord.yaml is not readable", html, StringComparison.Ordinal);
        Assert.Contains("UNKNOWN", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_peer_snapshot_says_so_next_to_its_age()
    {
        PairView view = Pairs.Healthy(Now) with { PeerCapturedAt = Now.AddMinutes(-10) };

        string html = Render(Page(view));

        Assert.Contains("STALE", html, StringComparison.Ordinal);
        Assert.Contains("10m00s", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_note_from_the_reading_is_shown_rather_than_dropped()
    {
        DashboardView page = DashboardView.Of(
            Pairs.Healthy(Now), Report(), OfflineAfter, Now, ["free space could not be read"]);

        Assert.Contains("free space could not be read", Render(page), StringComparison.Ordinal);
    }

    private static string Render(DashboardView page) => DashboardRenderer.Render(page, Refresh);

    private static DashboardView Healthy() => Page(Pairs.Healthy(Now), Report());

    private static DashboardView Broken() => Page(Pairs.Healthy(Now), Report(Critical()));

    private static DashboardView Page(PairView view, CheckReport? report = null) =>
        DashboardView.Of(view, report ?? Report(), OfflineAfter, Now, []);

    private static CheckReport Report(params Finding[] findings) =>
        new(
            Now,
            OperatingMode.Normal,
            Pairs.Source,
            Pairs.Target,
            true,
            findings,
            Feasibility.Calculate([], Pairs.TargetHost(Now), 4),
            []);

    private static Finding Critical() =>
        new(
            CheckRules.ById(CheckRules.ReplicaSwitchMismatch)!,
            "VM-DC-01",
            "attached to vSwitch-OLD",
            "this VM would boot with no network on the target",
            "Connect-VMNetworkAdapter -VMName VM-DC-01 -SwitchName vSwitch-PROD",
            FindingVerdict.Violated);

    private static Finding Unevaluable() =>
        new(
            CheckRules.ById(CheckRules.VlanMismatch)!,
            "VM-LEGACY-01",
            "the target's copy could not be read",
            "this rule could not be checked",
            null,
            FindingVerdict.Unevaluable);
}
