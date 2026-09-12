using System.Globalization;

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
}
