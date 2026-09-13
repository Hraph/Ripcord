namespace Ripcord.Domain;

/// Text that came from somewhere else, made safe to put in front of a human.
///
/// A VM name crosses the pair channel from the other host and is printed on a terminal during
/// an incident. A name carrying an ANSI escape can move the cursor, erase the line above it,
/// or paint a second verdict over the real one — and the whole tool exists to keep a confident
/// wrong answer off that screen. The same strings reach a mail subject, where a carriage
/// return is a header of the sender's choosing.
///
/// Replaced rather than dropped, so a tampered name is visible as tampered instead of quietly
/// reading as the name it was imitating.
public static class Printable
{
    public const char Replacement = '?';

    public static string Of(string? text)
    {
        if (text is null)
        {
            return "";
        }

        // The common case allocates nothing: these strings are host and VM names, and almost
        // all of them are already plain.
        return text.Any(IsUnprintable)
            ? string.Create(text.Length, text, static (span, source) =>
                {
                    for (int index = 0; index < source.Length; index++)
                    {
                        span[index] = IsUnprintable(source[index]) ? Replacement : source[index];
                    }
                })
            : text;
    }

    /// Null stays null. An absent switch name means "bound to no switch", which is a rule of
    /// its own; an empty string would be a different fact wearing the same shape.
    public static string? OrNull(string? text) => text is null ? null : Of(text);

    /// C0, DEL and C1. Tab and newline included: this is one-line text in a fixed-width
    /// column, and a tab in it is a column boundary moving.
    private static bool IsUnprintable(char value) =>
        value < ' ' || value == (char)0x7F || (value >= (char)0x80 && value <= (char)0x9F);
}
