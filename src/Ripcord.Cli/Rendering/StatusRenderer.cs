using Ripcord.Domain;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;
using System.Text;

namespace Ripcord.Cli.Rendering;

/// Fixed columns, fixed width, ASCII only, no colour. The real reading conditions are a
/// 1024×768 KVM during an incident: nothing may depend on the terminal being wide, on a
/// code page, or on the operator distinguishing two shades of red.
public static class StatusRenderer
{
    public const int Width = Layout.Width;

    private const int Indent = Layout.Indent;
    private const int LabelColumn = 8;
    private const int NameColumn = 20;
    private const int RoleColumn = 8;
    private const int StateColumn = 16;
    private const int HealthColumn = 8;
    private const int LagColumn = 6;
    private const int PendingColumn = 8;

    public static string Render(
        PairView view,
        TimeSpan offlineAfter,
        DateTimeOffset now,
        string? updateNotice = null,
        Palette? palette = null,
        ListenerAlert? listener = null,
        ListenerRunning? running = null)
    {
        ArgumentNullException.ThrowIfNull(view);

        StringBuilder output = new();

        output.AppendLine(Banner(now));
        output.AppendLine(VersionLine(view.PeerBuild));
        AppendUpdateNotice(output, updateNotice);
        output.AppendLine();
        AppendHost(output, "LOCAL", view.Local, offlineAfter, now);
        output.AppendLine();
        AppendHost(output, "PEER", view.Peer, offlineAfter, now, view.PeerCapturedAt);
        AppendListener(output, listener, running);

        return Layout.Rendered(output, palette);
    }

    /// Last, after both hosts: it explains what the other host will show about this one,
    /// which only this side can see.
    private static void AppendListener(
        StringBuilder output, ListenerAlert? alert, ListenerRunning? running)
    {
        if (alert is null)
        {
            AppendRunning(output, running);
            return;
        }

        string headline = alert.Critical ? Ink.Red(alert.Headline) : Ink.Amber(alert.Headline);

        output.AppendLine();
        output.AppendLine(
            Ink.Bold(Pad("LISTENER", LabelColumn + 2)) + headline);

        foreach (string line in Layout.Wrap(Sentence(alert.Reason), Width - Indent))
        {
            output.AppendLine(new string(' ', Indent) + line);
        }

        output.AppendLine($"{new string(' ', Indent)}Next: {alert.Next}");
    }

    /// One line when nothing is wrong: a running listener is said, not inferred from silence.
    private static void AppendRunning(StringBuilder output, ListenerRunning? running)
    {
        if (running is null)
        {
            return;
        }

        string state = running.Starting ? "starting" : "running";
        string build = running.Build ?? "";

        output.AppendLine();
        output.AppendLine(
            Ink.Bold(Pad("LISTENER", LabelColumn + 2))
            + Ink.Green(Pad(state, Width - LabelColumn - 2 - build.Length))
            + build);

        if (running.Outdated)
        {
            output.AppendLine(
                new string(' ', Indent)
                + Ink.Amber("Not the build of this ripcord.exe.")
                + " Next: ripcord service restart");
        }
    }

    /// Beside the version line, because that is what it is about, and below the banner
    /// rather than above it: an update is never the most important thing on this screen.
    internal static void AppendUpdateNotice(StringBuilder output, string? notice)
    {
        if (notice is null)
        {
            return;
        }

        foreach (string line in Layout.Wrap(notice, Layout.Width - Layout.Indent))
        {
            output.AppendLine(Layout.Spaces(Layout.Indent) + line);
        }
    }

    private static string Banner(DateTimeOffset now) =>
        Ink.Bold(Pad("RIPCORD STATUS", Width - TimestampOf(now).Length))
        + Ink.Faint(TimestampOf(now));

    /// Version skew between the two hosts has to be visible: the sequences are encoded in the
    /// binary and they span the pair.
    /// Both builds when the peer has published one.
    ///
    /// A failover spanning two hosts is refused outright on a mismatch, and until now the only
    /// way to discover the mismatch was to be refused by it. The skew is a fact about the pair,
    /// so it belongs on the page that describes the pair — not in the error message of the
    /// command somebody typed at 3 a.m.
    private static string VersionLine(BuildIdentity? peer)
    {
        string local = BuildInfo.VersionWithCommit;

        if (peer is null)
        {
            return $"ripcord {local}";
        }

        return peer.ToString() == local
            ? $"ripcord {local} on both hosts"
            : $"ripcord {local} here, {peer} on the peer - "
                + Ink.Red("a failover spanning both is refused");
    }

    private static void AppendHost(
        StringBuilder output,
        string label,
        HostState host,
        TimeSpan offlineAfter,
        DateTimeOffset now,
        DateTimeOffset? capturedAt = null)
    {
        string presence = PresenceOf(host, offlineAfter, now);

        // NetBIOS caps host names at 15, but the column is guarded rather than trusted.
        string name = Truncate(host.HostName, Width - LabelColumn - presence.Length - 1);

        // Padded first, coloured second. Wrapping the label *before* the outer Pad put two
        // markers inside a width calculation and the line came out two columns short — with
        // the colour off as well, since the markers are only removed on the way out.
        string head = Pad($"{Pad(label, LabelColumn)}{name}", Width - presence.Length);

        output.AppendLine(Ink.Bold(head) + Tint(presence));

        if (!host.IsReachable)
        {
            AppendUnreachable(output, host, now);
            return;
        }

        // The peer answers with a snapshot, never live (decision D18). Saying how old it is
        // beats the illusion of live data: four minutes old is information, not a defect.
        if (capturedAt is { } captured)
        {
            HostSnapshot snapshot = new(captured, host);

            output.AppendLine(
                $"{new string(' ', Indent)}State as of {TimestampOf(captured)} "
                + $"({Duration(snapshot.AgeAt(now))} old)"
                + (snapshot.IsFreshAt(now, offlineAfter) ? "" : Ink.Amber(" - STALE")));
        }

        if (host.Vms.Count == 0)
        {
            output.AppendLine($"{new string(' ', Indent)}No virtual machine on this host.");
            return;
        }

        output.AppendLine(Ink.Faint(Row("VM", "ROLE", "STATE", "HEALTH", "LAG", "PENDING")));
        output.AppendLine(Ink.Faint(new string(' ', Indent) + new string('-', Width - Indent)));

        foreach (VmReplicationState vm in host.Vms.OrderBy(
            vm => vm.Name, StringComparer.OrdinalIgnoreCase))
        {
            output.AppendLine(Row(
                Truncate(vm.Name, NameColumn),
                vm.Role.ToString(),
                StateLabel(vm.State),
                vm.Health.ToString(),
                Duration(vm.LagAt(now)),
                Bytes(vm.PendingBytes),
                vm.Health));
        }
    }

    /// Why, and since when. A peer that was never contacted has no "since": saying so is
    /// honest, inventing a timestamp is not.
    private static void AppendUnreachable(
        StringBuilder output, HostState host, DateTimeOffset now)
    {
        string indent = new(' ', Indent);

        output.AppendLine($"{indent}{Ink.Red(Sentence(host.Reachability.Reason))}");

        output.AppendLine(host.Reachability.UnreachableSince is { } since
            ? $"{indent}Unreachable since: {TimestampOf(since)} "
                + $"({Duration(host.Reachability.UnreachableFor(now))} ago)"
            : $"{indent}Unreachable since: never contacted");
    }

    private static string PresenceOf(HostState host, TimeSpan offlineAfter, DateTimeOffset now) =>
        host.Reachability.Presence(offlineAfter, now) switch
        {
            PeerPresence.Reachable => "REACHABLE",
            PeerPresence.Silent => "SILENT",
            _ => "OFFLINE",
        };

    /// Every cell is padded first and coloured second, so the colour cannot change a width.
    private static string Row(
        string name,
        string role,
        string state,
        string health,
        string lag,
        string pending,
        ReplicationHealth? severity = null) =>
        new string(' ', Indent)
        + Pad(name, NameColumn) + ' '
        + Pad(role, RoleColumn) + ' '
        + Pad(state, StateColumn) + ' '
        + Tint(Pad(health, HealthColumn), severity) + "  "
        + PadLeft(lag, LagColumn) + "  "
        + PadLeft(pending, PendingColumn);

    /// Colour says the same thing the word already says. Nothing here is only a colour: the
    /// block is read redirected to a file and by somebody who does not see red.
    private static string Tint(string cell, ReplicationHealth? severity) =>
        severity switch
        {
            ReplicationHealth.Normal => Ink.Green(cell),
            ReplicationHealth.Warning => Ink.Amber(cell),
            ReplicationHealth.Critical => Ink.Red(cell),
            _ => cell,
        };

    private static string Tint(string presence) =>
        presence switch
        {
            "REACHABLE" => Ink.Green(presence),
            "SILENT" => Ink.Amber(presence),
            _ => Ink.Red(presence),
        };

    /// Reasons carried from Windows or a socket can bring their own line breaks and full stop.
    private static string Sentence(string reason)
    {
        string text = string.Join(' ', reason.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return text.Length == 0
            ? ""
            : char.ToUpperInvariant(text[0]) + text[1..] + (text.EndsWith('.') ? "" : ".");
    }

    private static string TimestampOf(DateTimeOffset instant) => Layout.Timestamp(instant);

    private static string StateLabel(ReplicationState state) => Layout.StateLabel(state);

    private static string Duration(TimeSpan? span) => Layout.Duration(span);

    private static string Bytes(long? bytes) => Layout.Bytes(bytes);

    private static string Truncate(string value, int width) => Layout.Truncate(value, width);

    private static string Pad(string value, int width) => Layout.Pad(value, width);

    private static string PadLeft(string value, int width) => Layout.PadLeft(value, width);
}
