using System.Globalization;

namespace Ripcord.Domain.Diagnostics;

/// Which diagnostic lines to write when the same failure comes back every fifteen seconds.
///
/// The first of a kind is written in full, exception and all. The same line again within the
/// hour is counted, not written. The first one after the hour is written in full again, after
/// a line saying how many were held back. A failure that lasts all day is then one full entry
/// an hour, not five thousand.
/// Operations in `exempt` are always written: a line that is itself a summary must not be
/// summarised again.
public sealed class RepeatedEntries(TimeSpan window, IReadOnlyList<string>? exempt = null)
{
    public const int MaxKinds = 256;

    private readonly Dictionary<(string Operation, string Message), (DateTimeOffset Since, int Held)> seen = [];

    public IReadOnlyList<DiagnosticEntry> Admit(DiagnosticEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);

        (string, string) key = (entry.Operation, entry.Message);
        List<DiagnosticEntry> lines = [];

        if (exempt?.Contains(entry.Operation, StringComparer.Ordinal) == true)
        {
            lines.AddRange(this.Forget(now, key));
            lines.Add(entry);
            return lines;
        }

        if (this.seen.TryGetValue(key, out (DateTimeOffset Since, int Held) known) && now - known.Since < window)
        {
            this.seen[key] = known with { Held = known.Held + 1 };
            return [];
        }

        this.seen.Remove(key);
        lines.AddRange(this.Forget(now, key));
        this.seen[key] = (now, 0);

        if (known.Held > 0)
        {
            lines.Add(DiagnosticEntry.Of(entry.Operation, HeldBack(known.Held, known.Since)));
        }

        lines.Add(entry);
        return lines;
    }

    /// Kinds past their window are dropped, and the table never grows past `MaxKinds`: a
    /// message that embeds a changing value must not become a leak. A kind dropped with repeats
    /// held back says how many first, so a failure that stopped coming back is not left looking
    /// like a single occurrence.
    private List<DiagnosticEntry> Forget(DateTimeOffset now, (string, string) keep)
    {
        List<DiagnosticEntry> said = [];

        foreach (((string Operation, string Message) key, (DateTimeOffset Since, int Held) value) in this.seen
            .Where(pair => now - pair.Value.Since >= window && !pair.Key.Equals(keep))
            .ToList())
        {
            said.AddRange(Dropped(key, value));
            this.seen.Remove(key);
        }

        while (this.seen.Count >= MaxKinds)
        {
            KeyValuePair<(string Operation, string Message), (DateTimeOffset Since, int Held)> oldest =
                this.seen.MinBy(pair => pair.Value.Since);
            said.AddRange(Dropped(oldest.Key, oldest.Value));
            this.seen.Remove(oldest.Key);
        }

        return said;
    }

    private static IEnumerable<DiagnosticEntry> Dropped(
        (string Operation, string Message) key, (DateTimeOffset Since, int Held) value) =>
        value.Held > 0
            ? [DiagnosticEntry.Of(
                key.Operation,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"'{key.Message}' came {value.Held} more time(s) after {value.Since.UtcDateTime:HH:mm:ss} UTC"))]
            : [];

    private static string HeldBack(int held, DateTimeOffset since) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"the next line came {held} more time(s) since {since.UtcDateTime:HH:mm:ss} UTC");
}
