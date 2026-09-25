namespace Ripcord.Domain.Configuration;

/// Takes out of the carried `checks` lines the acknowledgements of VMs the new file no longer
/// declares. The validator refuses a file that keeps one, so carrying them as they were
/// wrote a file every command refused. Only block-style entries are recognised; anything else
/// is left as it was, and reported as still there.
public static class AcknowledgementPruning
{
    public sealed record Pruned(IReadOnlyList<string> Lines, IReadOnlyList<string> DroppedVms);

    public static Pruned Prune(IReadOnlyList<string> checks, IReadOnlyCollection<string> declared)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(declared);

        int key = -1;

        for (int index = 0; index < checks.Count; index++)
        {
            if (checks[index].Trim() == "acknowledgements:")
            {
                key = index;
                break;
            }
        }

        if (key < 0)
        {
            return new Pruned(checks, []);
        }

        List<string> kept = [.. checks.Take(key + 1)];
        List<string> dropped = [];
        int keyIndent = Indent(checks[key]);
        int? itemIndent = null;
        int keptItems = 0;
        int position = key + 1;

        while (position < checks.Count)
        {
            string line = checks[position];

            if (IsBlankOrComment(line))
            {
                kept.Add(line);
                position++;
                continue;
            }

            int indent = Indent(line);

            if (!IsItem(line) || indent < keyIndent || (itemIndent is { } expected && indent != expected))
            {
                break;
            }

            itemIndent = indent;

            int end = position + 1;

            while (end < checks.Count
                && (checks[end].Trim().Length == 0 || Indent(checks[end]) > indent)
                && !(checks[end].Trim().Length == 0 && NextIsNotInItem(checks, end, indent)))
            {
                end++;
            }

            List<string> item = [.. checks.Skip(position).Take(end - position)];

            if (VmOf(item) is { } vm
                && !declared.Contains(vm, StringComparer.OrdinalIgnoreCase))
            {
                dropped.Add(vm);
            }
            else
            {
                kept.AddRange(item);
                keptItems++;
            }

            position = end;
        }

        if (dropped.Count == 0)
        {
            return new Pruned(checks, []);
        }

        // An empty key would read as null; an empty list says what happened.
        if (keptItems == 0)
        {
            kept[key] = kept[key].TrimEnd() + " []";
        }

        kept.AddRange(checks.Skip(position));

        return new Pruned(kept, dropped);
    }

    private static bool NextIsNotInItem(IReadOnlyList<string> lines, int blank, int indent)
    {
        for (int index = blank + 1; index < lines.Count; index++)
        {
            if (lines[index].Trim().Length > 0)
            {
                return Indent(lines[index]) <= indent;
            }
        }

        return true;
    }

    private static string? VmOf(IReadOnlyList<string> item)
    {
        foreach (string line in item)
        {
            string text = line.Trim();

            if (text.StartsWith('-'))
            {
                text = text[1..].TrimStart();
            }

            if (!text.StartsWith("vm:", StringComparison.Ordinal))
            {
                continue;
            }

            string value = text[3..];
            int comment = value.IndexOf(" #", StringComparison.Ordinal);

            if (comment >= 0)
            {
                value = value[..comment];
            }

            value = value.Trim().Trim('"', '\'');
            return value.Length > 0 ? value : null;
        }

        return null;
    }

    private static bool IsItem(string line)
    {
        string text = line.TrimStart();
        return text == "-" || text.StartsWith("- ", StringComparison.Ordinal);
    }

    private static bool IsBlankOrComment(string line) =>
        line.Trim().Length == 0 || line.TrimStart().StartsWith('#');

    private static int Indent(string line) => line.Length - line.TrimStart().Length;
}
