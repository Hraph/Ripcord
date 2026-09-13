using System.Globalization;
using System.Net;
using System.Text;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Dashboard;
using Ripcord.Domain.Replication;

namespace Ripcord.Cli.Rendering;

/// The console layout, in a browser. One self-contained document: no script, no stylesheet,
/// no image, nothing fetched from anywhere — these hosts have no outbound access by design,
/// and a page that degrades to unstyled markup the moment the network goes is a page that
/// degrades exactly when it is being read.
///
/// Everything that came off the hypervisor is escaped on the way out. A VM name is data; the
/// one thing a read-only page must never do is execute what it was told to display.
public static class DashboardRenderer
{
    public static string Render(DashboardView page, TimeSpan refresh)
    {
        ArgumentNullException.ThrowIfNull(page);

        StringBuilder html = new();

        AppendHead(html, page, refresh);

        html.AppendLine("<body>");
        html.AppendLine("<h1>RIPCORD</h1>");
        AppendVerdict(html, page);
        AppendNotes(html, page);

        if (page.Local is { } local)
        {
            AppendHost(html, "LOCAL", local, page.At);
        }

        if (page.Peer is { } peer)
        {
            AppendHost(html, "PEER", peer, page.At);
        }

        AppendFindings(html, "CRITICAL", page.Criticals);
        AppendFindings(html, "NOT CHECKED", page.Unevaluated);
        AppendFindings(html, "WARNING", page.Warnings);
        AppendFindings(html, "INFORMATION", page.Information);

        AppendFooter(html, page, refresh);

        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return Layout.Rendered(html);
    }

    private static void AppendHead(StringBuilder html, DashboardView page, TimeSpan refresh)
    {
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"en\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");

        // In the document rather than in a script: the page has to keep refreshing with
        // scripting switched off, which is how a hardened host is configured.
        html.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"<meta http-equiv=\"refresh\" content=\"{(int)refresh.TotalSeconds}\">"));

        html.AppendLine($"<title>Ripcord - {Escape(Word(page.Verdict))}</title>");
        html.AppendLine("<style>");
        html.AppendLine(Style);
        html.AppendLine("</style>");
        html.AppendLine("</head>");
    }

    /// Spelled out, never only coloured. The KVM these hosts are read on is not a screen
    /// anyone should have to distinguish two shades of red on (rule 6).
    private static void AppendVerdict(StringBuilder html, DashboardView page)
    {
        string kind = page.Verdict.ToString().ToLowerInvariant();

        html.AppendLine($"<p class=\"verdict {kind}\">{Escape(Word(page.Verdict))}</p>");
        html.AppendLine($"<p class=\"headline\">{Escape(page.Headline)}</p>");
    }

    private static void AppendNotes(StringBuilder html, DashboardView page)
    {
        if (page.Notes.Count == 0)
        {
            return;
        }

        html.AppendLine("<ul class=\"notes\">");

        foreach (string note in page.Notes)
        {
            html.AppendLine($"<li>{Escape(note)}</li>");
        }

        html.AppendLine("</ul>");
    }

    private static void AppendHost(StringBuilder html, string label, HostPanel host, DateTimeOffset now)
    {
        html.AppendLine("<section>");

        html.AppendLine(
            $"<h2>{Escape(label)} {Escape(host.HostName)} "
            + $"<span class=\"presence\">{Escape(Word(host.Presence))}</span></h2>");

        if (host.Presence != PeerPresence.Reachable)
        {
            html.AppendLine($"<p class=\"reason\">{Escape(host.Reason ?? "no reason given")}.</p>");
            html.AppendLine("</section>");
            return;
        }

        // The peer answers with a snapshot, never live (decision D18). How old it is belongs
        // beside it: four minutes old is information, an undated reading is a claim.
        if (host.CapturedAt is { } captured)
        {
            html.AppendLine(
                $"<p class=\"captured\">State as of {Escape(Layout.Timestamp(captured))} "
                + $"({Escape(Layout.Duration(host.Age))} old)"
                + (host.IsStale ? " <strong>STALE</strong>" : "")
                + "</p>");
        }

        if (host.Vms.Count == 0)
        {
            html.AppendLine("<p>No virtual machine on this host.</p>");
            html.AppendLine("</section>");
            return;
        }

        AppendVmTable(html, host.Vms, now);
        html.AppendLine("</section>");
    }

    private static void AppendVmTable(
        StringBuilder html, IReadOnlyList<VmReplicationState> vms, DateTimeOffset now)
    {
        html.AppendLine("<table>");
        html.AppendLine(
            "<tr><th>VM</th><th>Role</th><th>State</th><th>Health</th>"
            + "<th class=\"right\">Lag</th><th class=\"right\">Pending</th></tr>");

        foreach (VmReplicationState vm in vms.OrderBy(
            vm => vm.Name, StringComparer.OrdinalIgnoreCase))
        {
            html.AppendLine(
                "<tr>"
                + $"<td>{Escape(vm.Name)}</td>"
                + $"<td>{Escape(vm.Role.ToString())}</td>"
                + $"<td>{Escape(Layout.StateLabel(vm.State))}</td>"
                + $"<td class=\"{vm.Health.ToString().ToLowerInvariant()}\">"
                + $"{Escape(vm.Health.ToString())}</td>"
                + $"<td class=\"right\">{Escape(Layout.Duration(vm.LagAt(now)))}</td>"
                + $"<td class=\"right\">{Escape(Layout.Bytes(vm.PendingBytes))}</td>"
                + "</tr>");
        }

        html.AppendLine("</table>");
    }

    /// The same three lines the console prints: what was observed, what it means at the
    /// moment of the failover, and the command that fixes it. The middle one is the reason
    /// this page is worth serving at all.
    private static void AppendFindings(
        StringBuilder html, string title, IReadOnlyList<Finding> findings)
    {
        if (findings.Count == 0)
        {
            return;
        }

        html.AppendLine("<section>");
        html.AppendLine($"<h2>{Escape(title)}</h2>");

        foreach (Finding finding in findings)
        {
            html.AppendLine("<div class=\"finding\">");

            html.AppendLine(
                $"<p class=\"rule\">{Escape(finding.Rule.Title)}"
                + (finding.Subject is { } subject ? $" - {Escape(subject)}" : "")
                + "</p>");

            html.AppendLine($"<p class=\"observed\">{Escape(finding.Observed)}</p>");
            html.AppendLine($"<p class=\"implication\">{Escape(finding.Implication)}</p>");

            if (finding.Remedy is { } remedy)
            {
                html.AppendLine($"<pre class=\"remedy\">{Escape(remedy)}</pre>");
            }

            if (finding.Suppression is { } suppression)
            {
                html.AppendLine(
                    $"<p class=\"acknowledged\">Acknowledged: {Escape(suppression.Acknowledgement.Reason)} "
                    + $"({(suppression.IsActive ? "expires" : "EXPIRED")} "
                    + $"{Escape(Layout.Date(suppression.Acknowledgement.Expires))})</p>");
            }

            html.AppendLine("</div>");
        }

        html.AppendLine("</section>");
    }

    private static void AppendFooter(StringBuilder html, DashboardView page, TimeSpan refresh)
    {
        html.AppendLine(
            $"<p class=\"footer\">Read {Escape(Layout.Timestamp(page.At))}, "
            + $"reloading every {(int)refresh.TotalSeconds}s. "
            + $"ripcord {Escape(BuildInfo.VersionWithCommit)}. "
            + "Read-only: nothing on this page changes anything.</p>");
    }

    private static string Word(DashboardVerdict verdict) => verdict switch
    {
        DashboardVerdict.Ready => "READY",
        DashboardVerdict.Broken => "NOT READY",
        DashboardVerdict.Degraded => "DEGRADED",
        _ => "UNKNOWN",
    };

    private static string Word(PeerPresence presence) => presence switch
    {
        PeerPresence.Reachable => "REACHABLE",
        PeerPresence.Silent => "SILENT",
        _ => "OFFLINE",
    };

    private static string Escape(string value) => WebUtility.HtmlEncode(value);

    /// Large type, high contrast, one column. The reading conditions are the same 1024×768
    /// KVM the console renderers are built for, and nothing here depends on colour alone.
    private const string Style = """
        :root { color-scheme: light dark; }
        body { font: 16px/1.5 monospace; margin: 1rem auto; max-width: 62rem; padding: 0 1rem; }
        h1 { font-size: 1.2rem; letter-spacing: 0.3rem; margin: 0; }
        h2 { border-bottom: 1px solid; font-size: 1rem; margin: 1.5rem 0 0.5rem; }
        p { margin: 0.3rem 0; }
        .verdict { font-size: 2rem; font-weight: bold; margin: 0.5rem 0 0; }
        .verdict.broken, .verdict.unknown { text-decoration: underline; }
        .headline { font-size: 1.1rem; }
        .presence { float: right; font-weight: normal; }
        .notes li, .reason, .captured, .footer { font-size: 0.9rem; }
        table { border-collapse: collapse; width: 100%; }
        th, td { border-bottom: 1px solid; padding: 0.2rem 0.4rem; text-align: left; }
        th { font-weight: bold; }
        .right { text-align: right; }
        .critical, .warning { font-weight: bold; }
        .finding { margin: 0.8rem 0; }
        .rule { font-weight: bold; }
        .remedy { margin: 0.2rem 0; overflow-x: auto; padding: 0.3rem 0; }
        .footer { border-top: 1px solid; margin-top: 2rem; padding-top: 0.5rem; }
        """;
}
