using System.Globalization;

namespace Ripcord.Domain.Diagnostics;

/// Which diagnostic lines to write when the same failure comes back every fifteen seconds.
///
/// The first of a kind is written in full, exception and all. The same line again within the
/// hour is counted, not written. The first one after the hour is written in full again, after
/// a line saying how many were held back. A failure that lasts all day is then one full entry
/// an hour, not five thousand.
public sealed class RepeatedEntries(TimeSpan window)
{
    public const int MaxKinds = 256;

    private readonly Dictionary<(string Operation, string Message), (DateTimeOffset Since, int Held)> seen = [];

    public IReadOnlyList<DiagnosticEntry> Admit(DiagnosticEntry entry, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);

        (string, string) key = (entry.Operation, entry.Message);

        if (this.seen.TryGetValue(key, out (DateTimeOffset Since, int Held) known) && now - known.Since < window)
        {
            this.seen[key] = known with { Held = known.Held + 1 };
            return [];
        }

        this.Forget(now);
        this.seen[key] = (now, 0);

        return known.Held > 0
            ? [DiagnosticEntry.Of(entry.Operation, HeldBack(known.Held, known.Since)), entry]
            : [entry];
    }

    /// Kinds past their window with nothing held back are dropped, and the table never grows
    /// past `MaxKinds`: a message that embeds a changing value must not become a leak.
    private void Forget(DateTimeOffset now)
    {
        foreach ((string, string) stale in this.seen
            .Where(pair => now - pair.Value.Since >= window && pair.Value.Held == 0)
            .Select(pair => pair.Key)
            .ToList())
        {
            this.seen.Remove(stale);
        }

        while (this.seen.Count >= MaxKinds)
        {
            this.seen.Remove(this.seen.MinBy(pair => pair.Value.Since).Key);
        }
    }

    private static string HeldBack(int held, DateTimeOffset since) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"the next line came {held} more time(s) since {since.UtcDateTime:HH:mm:ss} UTC");
}
