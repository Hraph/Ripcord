using System.Globalization;
using System.Text;
using Ripcord.Domain.Replication;

namespace Ripcord.Cli.Rendering;

/// Fixed columns, fixed width, ASCII only, no colour. The real reading conditions are a
/// 1024×768 KVM during an incident: nothing may depend on the terminal being wide, on a code
/// page, or on the operator distinguishing two shades of red.
///
/// Shared by both renderers so the two outputs line up when they are read one after the
/// other, which is how they will be.
internal static class Layout
{
    public const int Width = 75;

    public const int Indent = 2;

    /// The one place a rendered block becomes a string. The renderers build with
    /// StringBuilder.AppendLine, which is CRLF on Windows and LF in the Linux container the
    /// tests run in — and on a fixed 75-column layout a trailing carriage return is a 76th
    /// column. One format, whatever the host, decided here rather than by the framework.
    public static string Rendered(StringBuilder output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    public static string Banner(string title, DateTimeOffset now)
    {
        string timestamp = Timestamp(now);

        return Pad(title, Width - timestamp.Length) + timestamp;
    }

    public static string Timestamp(DateTimeOffset instant) =>
        instant.ToUniversalTime()
            .ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    public static string Date(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string Pad(string value, int width) => value.PadRight(width);

    public static string PadLeft(string value, int width) => value.PadLeft(width);

    public static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..(width - 3)] + "...";

    public static string Spaces(int count) => new(' ', count);

    public static string Line(int width) => new('-', width);

    /// A titled block, indented under its title and wrapped inside the fixed width. Shared so
    /// the indent and the wrap width are one decision rather than two that drift.
    public static void AppendBlock(StringBuilder output, string title, string body)
    {
        ArgumentNullException.ThrowIfNull(output);

        output.AppendLine($"  {title}");

        foreach (string line in Wrap(body, BlockWidth))
        {
            output.AppendLine("    " + line);
        }
    }

    /// The fixed width less the four columns a block is indented by, less one.
    private const int BlockWidth = 68;

    /// Wrapped on word boundaries at a fixed width, never at the terminal's. A word longer
    /// than the column — a path, a thumbprint — is broken rather than allowed to push the
    /// line off a 1024×768 screen.
    public static IEnumerable<string> Wrap(string text, int width)
    {
        if (width <= 0)
        {
            yield return text;
            yield break;
        }

        string remaining = text.Trim();

        while (remaining.Length > width)
        {
            int cut = remaining.LastIndexOf(' ', width);

            if (cut <= 0)
            {
                cut = width;
            }

            yield return remaining[..cut].TrimEnd();
            remaining = remaining[cut..].TrimStart();
        }

        if (remaining.Length > 0 || text.Trim().Length == 0)
        {
            yield return remaining;
        }
    }

    /// Null is "never replicated", which must never read as a lag of zero.
    public static string Duration(TimeSpan? span) => span switch
    {
        null => "-",
        { TotalSeconds: < 60 } value => $"{(int)value.TotalSeconds}s",
        { TotalHours: < 1 } value => $"{value.Minutes}m{value.Seconds:00}s",
        { TotalDays: < 1 } value => $"{(int)value.TotalHours}h{value.Minutes:00}m",
        // Capped, because PadLeft does not truncate and a months-old lag would push the
        // column beside it sideways.
        { TotalDays: >= 100 } => ">99d",
        { } value => $"{(int)value.TotalDays}d{value.Hours:00}h",
    };

    /// Null is "no relationship", not "nothing pending".
    public static string Bytes(long? bytes)
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

    /// The WMI state names are up to 28 characters; these fit a 16-column field without
    /// truncation, so no state ever reads as a prefix of another.
    public static string StateLabel(ReplicationState state) => state switch
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
}
