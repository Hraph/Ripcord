using System.Globalization;

namespace Ripcord.Domain.Alerting;

/// The window during which a notification waits rather than arrives. Written as
/// `22:00-07:00` in `ripcord.yaml` and read in the host's local time, so the instant it is
/// judged against carries its own offset — nothing here consults the machine clock.
///
/// The start is inside the window and the end is outside it: a run at exactly the end time
/// delivers what was held instead of holding it for another day.
public sealed record QuietHours(TimeOnly Start, TimeOnly End)
{
    public bool Covers(DateTimeOffset instant)
    {
        TimeOnly time = TimeOnly.FromTimeSpan(instant.TimeOfDay);

        // A window that wraps past midnight is the normal case — that is when nobody is
        // meant to be woken.
        return this.Start < this.End
            ? time >= this.Start && time < this.End
            : time >= this.Start || time < this.End;
    }

    /// A window that starts where it ends says either "always" or "never" and there is no
    /// way to tell which, so it is refused rather than guessed at.
    public static bool TryParse(string? text, out QuietHours? window)
    {
        window = null;

        if (text is null)
        {
            return false;
        }

        string[] parts = text.Split('-');

        if (parts.Length != 2
            || !TryReadTime(parts[0], out TimeOnly start)
            || !TryReadTime(parts[1], out TimeOnly end)
            || start == end)
        {
            return false;
        }

        window = new QuietHours(start, end);
        return true;
    }

    public override string ToString() => $"{this.Start:HH\\:mm}-{this.End:HH\\:mm}";

    private static bool TryReadTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(
            text.Trim(), ["HH:mm", "H:mm"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out time);
}
