using Ripcord.Domain;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;
using System.Globalization;
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

    public static string Render(PairView view, TimeSpan offlineAfter, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(view);

        StringBuilder output = new();

        output.AppendLine(Banner(now));
        output.AppendLine(VersionLine());
        output.AppendLine();
        AppendHost(output, "LOCAL", view.Local, offlineAfter, now);
        output.AppendLine();
        AppendHost(output, "PEER", view.Peer, offlineAfter, now, view.PeerCapturedAt);

        return output.ToString();
    }

    private static string Banner(DateTimeOffset now) =>
        Pad("RIPCORD STATUS", Width - TimestampOf(now).Length) + TimestampOf(now);

    /// Version skew between the two hosts has to be visible: the sequences are encoded in the
    /// binary and they span the pair.
    private static string VersionLine() => $"ripcord {BuildInfo.VersionWithCommit}";

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

        output.AppendLine(
            Pad($"{Pad(label, LabelColumn)}{name}", Width - presence.Length) + presence);

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
                + (snapshot.IsFreshAt(now, offlineAfter) ? "" : " - STALE"));
        }

        if (host.Vms.Count == 0)
        {
            output.AppendLine($"{new string(' ', Indent)}No virtual machine on this host.");
            return;
        }

        output.AppendLine(Row("VM", "ROLE", "STATE", "HEALTH", "LAG", "PENDING"));
        output.AppendLine(new string(' ', Indent) + new string('-', Width - Indent));

        foreach (VmReplicationState vm in host.Vms.OrderBy(
            vm => vm.Name, StringComparer.OrdinalIgnoreCase))
        {
            output.AppendLine(Row(
                Truncate(vm.Name, NameColumn),
                vm.Role.ToString(),
                StateLabel(vm.State),
                vm.Health.ToString(),
                Duration(vm.LagAt(now)),
                Bytes(vm.PendingBytes)));
        }
    }

    /// Why, and since when. A peer that was never contacted has no "since": saying so is
    /// honest, inventing a timestamp is not.
    private static void AppendUnreachable(
        StringBuilder output, HostState host, DateTimeOffset now)
    {
        string indent = new(' ', Indent);

        output.AppendLine($"{indent}{Sentence(host.Reachability.Reason)}");

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

    private static string Row(
        string name, string role, string state, string health, string lag, string pending) =>
        new string(' ', Indent)
        + Pad(name, NameColumn) + ' '
        + Pad(role, RoleColumn) + ' '
        + Pad(state, StateColumn) + ' '
        + Pad(health, HealthColumn) + "  "
        + PadLeft(lag, LagColumn) + "  "
        + PadLeft(pending, PendingColumn);

    /// The WMI state names are up to 28 characters; these fit the column without truncation,
    /// so no state ever reads as a prefix of another.
    private static string StateLabel(ReplicationState state) => state switch
    {
        ReplicationState.ReadyForReplication => "Ready",
        ReplicationState.WaitingToCompleteInitialReplication => "Initial repl.",
        ReplicationState.RepurposeReplicationInProgress => "Repurposing",
        ReplicationState.PreparedForSyncReplication => "Prepared (sync)",
        ReplicationState.PreparedForGroupReverseReplication => "Prepared (rev.)",
        ReplicationState.DiskUpdateInProgress => "Disk update",
        ReplicationState.DiskUpdateCritical => "Disk upd. crit.",
        ReplicationState.FiredrillInProgress => "Firedrill",
        ReplicationState.SyncedReplicationComplete => "Synced",
        ReplicationState.WaitingToStartResynchronization => "Await resync",
        ReplicationState.ResynchronizationSuspended => "Resync susp.",
        ReplicationState.FailoverInProgress => "Failover",
        ReplicationState.FailbackInProgress => "Failback",
        ReplicationState.FailbackComplete => "Failback done",
        _ => state.ToString(),
    };

    /// Null is "never replicated", which must never read as a lag of zero.
    private static string Duration(TimeSpan? span) => span switch
    {
        null => "-",
        { TotalSeconds: < 60 } value => $"{(int)value.TotalSeconds}s",
        { TotalHours: < 1 } value => $"{value.Minutes}m{value.Seconds:00}s",
        { TotalDays: < 1 } value => $"{(int)value.TotalHours}h{value.Minutes:00}m",
        // Capped, because PadLeft does not truncate and a months-old lag would push the
        // PENDING column sideways.
        { TotalDays: >= 100 } => ">99d",
        { } value => $"{(int)value.TotalDays}d{value.Hours:00}h",
    };

    /// Null is "no relationship", not "nothing pending".
    private static string Bytes(long? bytes)
    {
        if (bytes is not { } value)
        {
            return "-";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double scaled = value;
        int unit = 0;

        while (scaled >= 1023.95 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return scaled < 10 && unit > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{scaled:0.0} {units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(scaled)} {units[unit]}");
    }

    private static string Sentence(string reason) =>
        char.ToUpperInvariant(reason[0]) + reason[1..] + ".";

    private static string TimestampOf(DateTimeOffset instant) => Layout.Timestamp(instant);

    private static string Truncate(string value, int width) => Layout.Truncate(value, width);

    private static string Pad(string value, int width) => Layout.Pad(value, width);

    private static string PadLeft(string value, int width) => Layout.PadLeft(value, width);
}
