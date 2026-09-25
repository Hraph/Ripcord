namespace Ripcord.Domain.Configuration;

/// A `ripcord.yaml` split into its top-level sections, as the original lines.
///
/// This exists so that re-running `ripcord init` cannot lose anything. The interview owns six
/// sections; everything else in the file — `listener`, `alerting`, `dashboard`, `diagnostics`,
/// and any key a later version adds that this binary has never heard of — is carried across as
/// **text**, byte for byte.
///
/// Text rather than the parsed model, deliberately. `YamlConfigStore` deserialises with
/// `IgnoreUnmatchedProperties`, so a round trip through `ConfigurationDocument` silently drops
/// every key it does not know, and re-serialising drops every comment. Both are things the
/// operator wrote on purpose, and a setup command that eats them is one nobody runs twice.
///
/// The split is a column-0 scan, which is all a top-level key can be in YAML. Column-0
/// comments and blank lines attach to the section *below* them: a paragraph above `alerting:`
/// was written about alerting, and moving it away from its section would be its own kind of
/// loss. An indented comment belongs to the section above it, whose keys it is indented under.
public sealed record YamlSection(string Key, IReadOnlyList<string> Lines)
{
    /// The indented comments that close the section — commented-out keys, usually. A
    /// regenerated section is written without them, so they are given back after it.
    public IReadOnlyList<string> Trailer
    {
        get
        {
            int end = this.Lines.Count;

            while (end > 1 && this.Lines[end - 1].Length == 0)
            {
                end--;
            }

            int start = end;

            while (start > 1 && YamlSections.IsCommentOrBlank(this.Lines[start - 1]))
            {
                start--;
            }

            while (start < end && this.Lines[start].Length == 0)
            {
                start++;
            }

            return [.. this.Lines.Skip(start).Take(end - start)];
        }
    }

    public bool Equals(YamlSection? other) =>
        other is not null && this.Key == other.Key && Structural.Same(this.Lines, other.Lines);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(this.Key);
        Structural.Add(ref hash, this.Lines);
        return hash.ToHashCode();
    }
}

public static class YamlSections
{
    public static IReadOnlyList<YamlSection> Split(string? text) => SplitAll(text).Sections;

    /// The column-0 comment block after the last section: examples of sections the file does
    /// not have, commented out. It belongs to no section, so regenerating one never loses it.
    public static IReadOnlyList<string> Epilogue(string? text) => SplitAll(text).Epilogue;

    private static (IReadOnlyList<YamlSection> Sections, IReadOnlyList<string> Epilogue) SplitAll(
        string? text)
    {
        if (text is null || text.Length == 0)
        {
            return ([], []);
        }

        List<YamlSection> sections = [];
        List<string> pending = [];
        string? key = null;

        foreach (string line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (TopLevelKey(line) is not { } found)
            {
                pending.Add(line);
                continue;
            }

            // The comment block above a key belongs to it, so it travels with it. What sits
            // above the *first* key is the file's own header, which the rendering replaces.
            int attached = AttachedFrom(pending);

            if (key is not null)
            {
                sections.Add(new YamlSection(key, [.. pending[..attached]]));
            }

            pending = [.. pending[attached..], line];
            key = found;
        }

        if (key is null)
        {
            return (sections, []);
        }

        int epilogue = AttachedFrom(pending);

        // Only a block with a comment in it: blank lines alone are just the end of the file.
        if (epilogue < pending.Count && pending[epilogue..].Any(line => line.Length > 0))
        {
            sections.Add(new YamlSection(key, [.. TrimmedTail(pending[..epilogue])]));
            return (sections, [.. TrimmedTail(pending[epilogue..])]);
        }

        sections.Add(new YamlSection(key, [.. TrimmedTail(pending)]));
        return (sections, []);
    }

    public static IReadOnlyList<YamlSection> Except(
        IReadOnlyList<YamlSection> sections, IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(keys);

        return [.. sections.Where(section => !keys.Contains(section.Key, StringComparer.Ordinal))];
    }

    /// `key:` or `key: value` at column 0. A list item, an indented key and a document marker
    /// are all excluded by the first character alone.
    private static string? TopLevelKey(string line)
    {
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#' || line[0] == '-')
        {
            return null;
        }

        int colon = line.IndexOf(':', StringComparison.Ordinal);

        return colon > 0 && line[..colon].All(IsKeyCharacter) ? line[..colon] : null;
    }

    private static bool IsKeyCharacter(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '-' or '.';

    /// Where the trailing comment block starts: everything from there down was written about
    /// the section that follows it, not the one above.
    private static int AttachedFrom(List<string> lines)
    {
        int index = lines.Count;

        while (index > 0 && IsColumnZeroCommentOrBlank(lines[index - 1]))
        {
            index--;
        }

        // A blank line immediately after a section still separates it from the comment block,
        // so it stays with the section it closes rather than opening the next one.
        return index < lines.Count && index > 0 && lines[index].Length == 0 ? index + 1 : index;
    }

    internal static bool IsCommentOrBlank(string line) =>
        line.Length == 0 || line.TrimStart().StartsWith('#');

    private static bool IsColumnZeroCommentOrBlank(string line) =>
        line.Length == 0 || line[0] == '#';

    /// One trailing blank line at most, so sections joined back together do not accumulate
    /// the whitespace at the end of the file.
    private static List<string> TrimmedTail(List<string> lines)
    {
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
