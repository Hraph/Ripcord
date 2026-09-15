using System.Text;

namespace Ripcord.Cli.Rendering;

/// How a rendered block is marked up, and how those marks become colour — or nothing.
///
/// The renderers never emit an escape sequence. They wrap a span in a one-character marker
/// (`Ink`), and the whole block passes through here exactly once, on its way out of
/// `Layout.Rendered`. Two reasons it is done that way rather than by writing escapes as the
/// text is built:
///
/// - **The log must stay readable.** The listener passes the same writer for output and error
///   into `listener.log`, and a redirected `ripcord status > state.txt` is a file somebody
///   reads. Escapes in either are noise nobody asked for, so the default is `None` and the
///   colour is switched on only where a console said it would understand it.
/// - **Columns must not move.** A marker is stripped before anything is measured, and the
///   layout is a fixed 75 columns whose alignment is asserted. Colouring a padded cell cannot
///   change its width because the padding happened first.
public sealed class Palette
{
    /// No colour at all: the markers are removed and the text is what it always was. The
    /// default everywhere, including every test.
    public static readonly Palette None = new(false);

    /// SGR escapes, for a console that has said it understands them.
    public static readonly Palette Ansi = new(true);

    private readonly bool coloured;

    private Palette(bool coloured) => this.coloured = coloured;

    public bool IsColoured => this.coloured;

    /// Replaces every marker with an escape sequence, or with nothing.
    public string Apply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.AsSpan().IndexOfAny(Ink.Markers) < 0)
        {
            return text;
        }

        StringBuilder applied = new(text.Length);

        foreach (char character in text)
        {
            if (Sequence(character) is { } sequence)
            {
                if (this.coloured)
                {
                    applied.Append(sequence);
                }
            }
            else
            {
                applied.Append(character);
            }
        }

        return applied.ToString();
    }

    private static string? Sequence(char marker) =>
        marker switch
        {
            Ink.Critical => "\u001b[31m",
            Ink.Warning => "\u001b[33m",
            Ink.Good => "\u001b[32m",
            Ink.Dim => "\u001b[2m",
            Ink.Heading => "\u001b[1m",
            Ink.Accent => "\u001b[36m",
            Ink.End => "\u001b[0m",
            _ => null,
        };
}

/// The markers themselves, and the one rule that makes them safe.
///
/// Every marker is a C0 control character, which is exactly what `Printable.Of` replaces with
/// `?` in any text that came from somewhere else — a VM name off the pair channel, a peer's
/// host name, a CIM error. So a name cannot carry a marker into a rendered block and paint
/// the line it sits on: the styling vocabulary is unreachable from data by construction,
/// rather than by a rule somebody has to remember.
internal static class Ink
{
    public const char Critical = '\u0011';

    public const char Warning = '\u0012';

    public const char Good = '\u0013';

    public const char End = '\u0014';

    public const char Dim = '\u0015';

    public const char Heading = '\u0016';

    public const char Accent = '\u0017';

    public static readonly System.Buffers.SearchValues<char> Markers =
        System.Buffers.SearchValues.Create([Critical, Warning, Good, End, Dim, Heading, Accent]);

    /// Wrap the text **after** it has been padded to its column width, never before: the
    /// padding is what the layout measures, and a marker inside it would be counted.
    public static string Red(string text) => Critical + text + End;

    public static string Amber(string text) => Warning + text + End;

    public static string Green(string text) => Good + text + End;

    public static string Faint(string text) => Dim + text + End;

    public static string Bold(string text) => Heading + text + End;

    public static string Cyan(string text) => Accent + text + End;
}
