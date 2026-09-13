using Ripcord.Domain.Checks;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;

namespace Ripcord.Domain.Dashboard;

/// What the page says before anything on it is read in detail. Four states rather than two,
/// because "I could not tell" and "everything is fine" are the pair of answers a dashboard
/// most easily confuses, and confusing them is how a green page stops meaning anything.
public enum DashboardVerdict
{
    Unknown,
    Broken,
    Degraded,
    Ready,
}

/// One side of the pair as the page shows it. An unreachable host carries no snapshot age:
/// the last thing it said is not a reading of what it is now.
public sealed record HostPanel(
    string HostName,
    PeerPresence Presence,
    string? Reason,
    DateTimeOffset? CapturedAt,
    TimeSpan? Age,
    bool IsStale,
    IReadOnlyList<VmReplicationState> Vms);

/// The whole page, decided here so the renderer only lays it out. Built from the two readings
/// `status` and `check` already produce — there is no second opinion on whether the pair is
/// healthy, and no rule evaluated twice.
///
/// Unlike the console commands, nobody typed this page and nobody is watching a terminal when
/// it refreshes. That is the whole of its difference: a reading that failed has to say so on
/// the page itself, because there is no stderr and no exit code to carry it.
public sealed record DashboardView(
    DateTimeOffset At,
    DashboardVerdict Verdict,
    string Headline,
    HostPanel? Local,
    HostPanel? Peer,
    IReadOnlyList<Finding> Criticals,
    IReadOnlyList<Finding> Unevaluated,
    IReadOnlyList<Finding> Warnings,
    IReadOnlyList<Finding> Information,
    IReadOnlyList<string> Notes)
{
    public static DashboardView Of(
        PairView view,
        CheckReport? report,
        TimeSpan offlineAfter,
        DateTimeOffset now,
        IReadOnlyList<string> notes)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(notes);

        HostPanel local = Panel(view.Local, null, offlineAfter, now);
        HostPanel peer = Panel(view.Peer, view.PeerCapturedAt, offlineAfter, now);

        List<string> reasons = Reasons(local, peer, report);
        DashboardVerdict verdict = Decide(local, report, reasons);

        return new DashboardView(
            now,
            verdict,
            Summarise(verdict, report, reasons),
            local,
            peer,
            [.. report?.Of(Severity.Critical) ?? []],
            [.. report?.Unevaluated ?? []],
            [.. report?.Of(Severity.Warning) ?? []],
            [.. report?.Of(Severity.Info) ?? []],
            notes);
    }

    /// The page could not be built at all. It still renders, and it names what stopped it:
    /// a blank page and a page still showing the previous reading are the two ways a
    /// dashboard lies, and both are worse than an error.
    public static DashboardView Unavailable(DateTimeOffset at, string reason) =>
        new(
            at,
            DashboardVerdict.Unknown,
            $"Nothing on this page is current: {reason}.",
            null,
            null,
            [],
            [],
            [],
            [],
            []);

    /// A violated critical outranks every other reason the page might be uncertain. Softening
    /// it to "unknown" because something else was also unreadable would bury the one finding
    /// that has to be acted on now.
    private static DashboardVerdict Decide(
        HostPanel local, CheckReport? report, List<string> reasons)
    {
        if (report is { HasCriticalViolation: true })
        {
            return DashboardVerdict.Broken;
        }

        if (local.Presence != PeerPresence.Reachable)
        {
            return DashboardVerdict.Unknown;
        }

        return reasons.Count > 0 ? DashboardVerdict.Degraded : DashboardVerdict.Ready;
    }

    private static List<string> Reasons(HostPanel local, HostPanel peer, CheckReport? report)
    {
        List<string> reasons = [];

        if (local.Presence != PeerPresence.Reachable)
        {
            reasons.Add("this host could not be read");
        }

        if (peer.Presence != PeerPresence.Reachable)
        {
            reasons.Add(peer.Presence == PeerPresence.Silent
                ? "the peer has gone quiet"
                : "the peer is offline");
        }
        else if (peer.IsStale)
        {
            reasons.Add("the peer's state is older than the offline threshold");
        }

        if (report is null)
        {
            reasons.Add("no check was run");
            return reasons;
        }

        int unevaluated = report.Unevaluated.Count();

        if (unevaluated > 0)
        {
            reasons.Add($"{unevaluated} {Rules(unevaluated)} could not be checked");
        }

        int warnings = report.Of(Severity.Warning).Count();

        if (warnings > 0)
        {
            reasons.Add($"{warnings} warning{Plural(warnings)}");
        }

        return reasons;
    }

    private static string Summarise(
        DashboardVerdict verdict, CheckReport? report, List<string> reasons)
    {
        string listed = string.Join(", ", reasons);

        return verdict switch
        {
            DashboardVerdict.Broken => Broken(report, reasons, listed),
            DashboardVerdict.Unknown => $"Nothing on this page is current: {listed}.",
            DashboardVerdict.Degraded => $"No critical finding, but {listed}.",
            _ => "No critical finding. A failover would work.",
        };
    }

    private static string Broken(
        CheckReport? report, List<string> reasons, string listed)
    {
        int criticals = report!.Findings.Count(finding => finding.CountsAsCritical);

        return $"{criticals} critical finding{Plural(criticals)} - a failover would not work"
            + (reasons.Count > 0 ? $", and {listed}." : ".");
    }

    private static HostPanel Panel(
        HostState host, DateTimeOffset? capturedAt, TimeSpan offlineAfter, DateTimeOffset now)
    {
        PeerPresence presence = host.Reachability.Presence(offlineAfter, now);

        if (presence != PeerPresence.Reachable)
        {
            return new HostPanel(
                host.HostName, presence, host.Reachability.Reason, null, null, false, []);
        }

        // The local host has no capture instant: it was read here, now. Only the peer answers
        // with a snapshot, and only a snapshot can be stale (decision D18).
        if (capturedAt is not { } captured)
        {
            return new HostPanel(host.HostName, presence, null, null, null, false, host.Vms);
        }

        HostSnapshot snapshot = new(captured, host);

        return new HostPanel(
            host.HostName,
            presence,
            null,
            captured,
            snapshot.AgeAt(now),
            !snapshot.IsFreshAt(now, offlineAfter),
            host.Vms);
    }

    private static string Plural(int count) => count == 1 ? "" : "s";

    private static string Rules(int count) => count == 1 ? "rule" : "rules";
}
