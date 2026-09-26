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
        AppendMasthead(html, page);
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

        AppendFindings(html, "CRITICAL", "alarm", page.Criticals);
        AppendFindings(html, "NOT CHECKED", "advisory", page.Unevaluated);
        AppendFindings(html, "WARNING", "caution", page.Warnings);
        AppendFindings(html, "INFORMATION", "info", page.Information);

        AppendFooter(html, page, refresh);

        html.AppendLine("</body>");
        html.AppendLine("</html>");

        return Layout.Rendered(html);
    }

    /// The wordmark and the instant the page was read. The time belongs above the fold: the
    /// first question anybody asks a self-refreshing page is how old it is.
    private static void AppendMasthead(StringBuilder html, DashboardView page)
    {
        html.AppendLine("<header class=\"masthead\">");
        html.AppendLine($"<h1>ripcord <span class=\"build\">{Escape(BuildInfo.Version)}</span></h1>");
        html.AppendLine($"<p class=\"read\">{Escape(Layout.Timestamp(page.At))}</p>");
        html.AppendLine("</header>");
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

        html.AppendLine($"<section class=\"verdict {kind}\">");
        html.AppendLine($"<p class=\"word\">{Escape(Word(page.Verdict))}</p>");
        html.AppendLine($"<p class=\"headline\">{Escape(page.Headline)}</p>");
        html.AppendLine("</section>");
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
        html.AppendLine("<section class=\"host-panel\">");

        html.AppendLine(
            $"<h2 class=\"panel-head\">{Escape(label)} <span class=\"host\">{Escape(host.HostName)}</span>"
            + $"<span class=\"presence {host.Presence.ToString().ToLowerInvariant()}\">"
            + $"{Escape(Word(host.Presence))}</span></h2>");

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
                + (host.IsStale ? " <strong class=\"stale\">STALE</strong>" : "")
                + "</p>");
        }

        if (host.Vms.Count == 0)
        {
            html.AppendLine("<p>No virtual machine on this host.</p>");
            html.AppendLine("</section>");
            return;
        }

        // A snapshot's lag is as of when it was taken: measured to now, it would grow with the
        // snapshot's age and show a healthy replication falling behind.
        AppendVmTable(html, host.Vms, host.CapturedAt ?? now);
        html.AppendLine("</section>");
    }

    private static void AppendVmTable(
        StringBuilder html, IReadOnlyList<VmReplicationState> vms, DateTimeOffset now)
    {
        html.AppendLine("<div class=\"scroll\">");
        html.AppendLine("<table>");
        html.AppendLine(
            "<tr><th>VM</th><th>Role</th><th>State</th><th>Health</th>"
            + "<th class=\"num\">Lag</th><th class=\"num\">Pending</th></tr>");

        foreach (VmReplicationState vm in vms.OrderBy(
            vm => vm.Name, StringComparer.OrdinalIgnoreCase))
        {
            html.AppendLine(
                "<tr>"
                + $"<td class=\"name\">{Escape(vm.Name)}</td>"
                + $"<td>{Escape(vm.Role.ToString())}</td>"
                + $"<td>{Escape(Layout.StateLabel(vm.State))}</td>"
                + $"<td class=\"{vm.Health.ToString().ToLowerInvariant()}\">"
                + $"{Escape(vm.Health.ToString())}</td>"
                + $"<td class=\"num\">{Escape(Layout.Duration(vm.LagAt(now)))}</td>"
                + $"<td class=\"num\">{Escape(Layout.Bytes(vm.PendingBytes))}</td>"
                + "</tr>");
        }

        html.AppendLine("</table>");
        html.AppendLine("</div>");
    }

    /// The same three lines the console prints: what was observed, what it means at the
    /// moment of the failover, and the command that fixes it. The middle one is the reason
    /// this page is worth serving at all.
    private static void AppendFindings(
        StringBuilder html, string title, string severity, IReadOnlyList<Finding> findings)
    {
        if (findings.Count == 0)
        {
            return;
        }

        html.AppendLine($"<section class=\"findings {severity}\">");
        html.AppendLine($"<h2 class=\"panel-head\">{Escape(title)}</h2>");

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
            $"<p class=\"footer\">Reloading every {(int)refresh.TotalSeconds}s. "
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

    /// The colour language is the one aircraft annunciator panels settled on: red means act
    /// now, amber means be aware, green means normal, a cool neutral means advisory. Borrowed
    /// rather than invented, because that is the vocabulary that was designed to be read by
    /// somebody under pressure — which is the only condition this page is read in.
    ///
    /// Colour is **redundant** encoding throughout. Every state is spelled out in words as
    /// well, so the page survives a bad KVM, a colour-blind reader and a black-and-white
    /// print (rule 6). Decoration and alarm are told apart by saturation, not hue: chrome is
    /// a wash behind text, a warning is full strength on the text itself.
    ///
    /// No motion of any kind. The page reloads itself every few seconds, and an entrance
    /// animation would be a flicker on every one of them.
    private const string Style = """
        :root {
          color-scheme: light dark;
          --ground: #EDEEF0; --panel: #FFFFFF; --zebra: #F6F7F9;
          --ink: #16181D; --muted: #5C636E; --rule: #D3D7DE;
          --accent: #2F5D8C; --accent-wash: #E3EAF2;
          --ok: #0E7C3F; --ok-wash: #E2F1E8;
          --caution: #B06E00; --caution-wash: #FBF0DC;
          --alarm: #C0231F; --alarm-wash: #FAE4E3;
          --advisory: #41566E; --advisory-wash: #E8ECF1;
          --sans: "Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system,
                  "Helvetica Neue", Arial, sans-serif;
          --mono: Consolas, "Cascadia Mono", "SF Mono", ui-monospace, Menlo, monospace;
        }

        @media (prefers-color-scheme: dark) {
          :root {
            --ground: #14171C; --panel: #1B1F26; --zebra: #1F242B;
            --ink: #E8EBEF; --muted: #98A1AE; --rule: #2C323B;
            --accent: #7FA6D0; --accent-wash: #1E2833;
            --ok: #3DD07A; --ok-wash: #14261C;
            --caution: #F0A81E; --caution-wash: #2A2213;
            --alarm: #FF5A52; --alarm-wash: #2E1917;
            --advisory: #8FA3BC; --advisory-wash: #1D232B;
          }
        }

        * { box-sizing: border-box; }

        body {
          background: var(--ground); color: var(--ink);
          font: 0.9375rem/1.55 var(--sans);
          margin: 0 auto; max-width: 64rem; padding: 1.25rem 1.25rem 2.5rem;
        }

        .masthead {
          align-items: baseline; border-bottom: 2px solid var(--accent);
          display: flex; gap: 1rem; justify-content: space-between;
          padding-bottom: 0.5rem;
        }

        h1 {
          color: var(--accent); font-size: 0.9rem; font-weight: 600;
          letter-spacing: 0.22em; margin: 0; text-transform: uppercase;
        }

        .build { color: var(--muted); font-weight: 400; letter-spacing: 0.08em; }

        .read { color: var(--muted); font-family: var(--mono); font-size: 0.8125rem; margin: 0; }

        /* The one loud thing on the page. Everything around it stays quiet so it stays loud. */
        .verdict {
          background: var(--advisory-wash); border-left: 6px solid var(--advisory);
          margin: 1.25rem 0; padding: 0.9rem 1.1rem;
        }

        .verdict .word {
          font-size: 2.5rem; font-weight: 600; letter-spacing: -0.02em;
          line-height: 1.1; margin: 0;
        }

        .verdict .headline {
          color: var(--ink); margin: 0.35rem 0 0; max-width: 68ch;
        }

        .verdict.ready { background: var(--ok-wash); border-color: var(--ok); }
        .verdict.ready .word { color: var(--ok); }
        .verdict.degraded { background: var(--caution-wash); border-color: var(--caution); }
        .verdict.degraded .word { color: var(--caution); }
        .verdict.broken { background: var(--alarm-wash); border-color: var(--alarm); }
        .verdict.broken .word { color: var(--alarm); }
        .verdict.unknown .word { color: var(--advisory); }

        section { margin: 1.5rem 0; }

        .panel-head {
          align-items: baseline; background: var(--accent-wash); border-radius: 2px;
          color: var(--accent); display: flex; font-size: 1.0625rem; font-weight: 600;
          gap: 0.5rem; letter-spacing: 0.08em; margin: 0 0 0.5rem;
          padding: 0.4rem 0.7rem; text-transform: uppercase;
        }

        .panel-head .host {
          color: var(--ink); font-family: var(--mono); letter-spacing: 0;
          text-transform: none;
        }

        .presence {
          font-size: 0.75rem; letter-spacing: 0.1em; margin-left: auto;
        }

        .presence.reachable { color: var(--ok); }
        .presence.silent { color: var(--caution); }
        .presence.offline { color: var(--alarm); }

        .notes, .reason, .captured { color: var(--muted); font-size: 0.8125rem; }
        .notes { margin: 0 0 1rem; padding-left: 1.1rem; }
        .reason, .captured { margin: 0 0 0.5rem; padding-left: 0.7rem; }
        .stale { color: var(--caution); letter-spacing: 0.08em; }

        .scroll { overflow-x: auto; }

        table {
          background: var(--panel); border: 1px solid var(--rule); border-collapse: collapse;
          font-size: 0.875rem; min-width: 38rem; width: 100%;
        }

        th {
          background: var(--panel); border-bottom: 1px solid var(--rule); color: var(--muted);
          font-size: 0.6875rem; font-weight: 600; letter-spacing: 0.1em;
          text-align: left; text-transform: uppercase;
        }

        th, td { padding: 0.35rem 0.7rem; }

        tr:nth-child(even) td { background: var(--zebra); }

        .name { font-family: var(--mono); font-weight: 600; }

        /* Tabular figures, because the page reloads itself and a lag column whose digits
           change width jitters on every refresh. */
        .num {
          font-family: var(--mono); font-variant-numeric: tabular-nums; text-align: right;
        }

        .findings .panel-head { background: var(--advisory-wash); color: var(--advisory); }
        .findings.alarm .panel-head { background: var(--alarm-wash); color: var(--alarm); }
        .findings.caution .panel-head { background: var(--caution-wash); color: var(--caution); }

        .finding {
          background: var(--panel); border: 1px solid var(--rule);
          border-left: 4px solid var(--advisory); margin: 0.5rem 0; padding: 0.6rem 0.9rem;
        }

        .findings.alarm .finding { border-left-color: var(--alarm); }
        .findings.caution .finding { border-left-color: var(--caution); }

        .rule { font-weight: 600; margin: 0; }
        .observed { color: var(--muted); margin: 0.15rem 0; max-width: 72ch; }
        .implication { margin: 0.15rem 0; max-width: 72ch; }
        .acknowledged { color: var(--muted); font-size: 0.8125rem; margin: 0.3rem 0 0; }

        .remedy {
          background: var(--ground); border: 1px solid var(--rule); font-family: var(--mono);
          font-size: 0.8125rem; margin: 0.4rem 0 0; overflow-x: auto; padding: 0.4rem 0.6rem;
        }

        .footer {
          border-top: 1px solid var(--rule); color: var(--muted); font-size: 0.8125rem;
          margin-top: 2rem; padding-top: 0.6rem;
        }
        """;
}
