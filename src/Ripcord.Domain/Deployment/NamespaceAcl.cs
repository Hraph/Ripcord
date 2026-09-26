namespace Ripcord.Domain.Deployment;

public enum NamespaceGrant
{
    Granted,
    Missing,

    /// Not something Ripcord will edit: a deny for the account, or a descriptor it does not
    /// fully understand.
    Unmodifiable,
}

/// A new descriptor, or the reason there is none.
public sealed record NamespaceEdit(string? Sddl, string? Refusal);

/// One ACE on a WMI namespace's access list, read and edited as SDDL, and nothing else.
///
/// The descriptor belongs to Windows and guards a system namespace, so every edit is refused
/// unless it is exactly one ACE added or removed, with owner, group, flags and every other ACE
/// unchanged and in order — checked on the result, not assumed.
public static class NamespaceAcl
{
    /// Enable account (0x1) and execute methods (0x2): reading the volumes and asking each one
    /// whether it unlocks itself. No remote access, no write, no inheritance.
    public static string Ace(string sid) => $"(A;;CCDC;;;{sid})";

    public static (NamespaceGrant State, string? Why) Read(string sddl, string sid)
    {
        if (Parse(sddl) is not { } parsed)
        {
            return (NamespaceGrant.Unmodifiable, "its access list is not one Ripcord edits");
        }

        if (parsed.Aces.Any(ace => IsFor(ace, sid) && Field(ace, 0) is "D"))
        {
            return (NamespaceGrant.Unmodifiable, "it denies the account explicitly");
        }

        return parsed.Aces.Any(ace => IsOurs(ace, sid))
            ? (NamespaceGrant.Granted, null)
            : (NamespaceGrant.Missing, null);
    }

    public static NamespaceEdit WithGrant(string sddl, string sid)
    {
        (NamespaceGrant state, string? why) = Read(sddl, sid);

        if (state == NamespaceGrant.Unmodifiable)
        {
            return new NamespaceEdit(null, why);
        }

        if (state == NamespaceGrant.Granted)
        {
            return new NamespaceEdit(sddl, null);
        }

        Descriptor parsed = Parse(sddl)!;

        // After the explicit entries, before the inherited ones: the canonical order.
        int at = parsed.Aces.FindIndex(ace => Field(ace, 1).Contains("ID", StringComparison.Ordinal));
        List<string> aces = [.. parsed.Aces];
        aces.Insert(at < 0 ? aces.Count : at, Ace(sid));

        return Checked(sddl, parsed with { Aces = aces }, parsed.Aces.Count + 1, sid);
    }

    public static NamespaceEdit WithoutGrant(string sddl, string sid)
    {
        if (Parse(sddl) is not { } parsed)
        {
            return new NamespaceEdit(null, "its access list is not one Ripcord edits");
        }

        List<string> aces = [.. parsed.Aces.Where(ace => !IsOurs(ace, sid))];

        return aces.Count == parsed.Aces.Count
            ? new NamespaceEdit(sddl, null)
            : Checked(sddl, parsed with { Aces = aces }, aces.Count, sid);
    }

    /// The result re-read and compared with what it was made from, before anything is written.
    private static NamespaceEdit Checked(string original, Descriptor edited, int expected, string sid)
    {
        string text = edited.Text();

        return Parse(text) is { } back
            && Parse(original) is { } before
            && back.Owner == before.Owner
            && back.Group == before.Group
            && back.Flags == before.Flags
            && back.Aces.Count == expected
            && before.Aces.Where(ace => !IsOurs(ace, sid))
                .SequenceEqual(back.Aces.Where(ace => !IsOurs(ace, sid)))
            ? new NamespaceEdit(text, null)
            : new NamespaceEdit(null, "the edited access list did not check out");
    }

    private static bool IsOurs(string ace, string sid) =>
        IsFor(ace, sid)
        && Field(ace, 0) == "A"
        && Field(ace, 1).Length == 0
        && Field(ace, 2) is "CCDC" or "0x3";

    private static bool IsFor(string ace, string sid) =>
        string.Equals(Field(ace, 5), sid, StringComparison.OrdinalIgnoreCase);

    private static string Field(string ace, int index) =>
        ace.Trim('(', ')').Split(';') is { Length: 6 } fields ? fields[index] : "";

    /// `O:..G:..D:flags(ace)(ace)...`, and nothing else: no SACL, no NULL or empty-marker DACL,
    /// no conditional ACE, no ACE that is not six fields.
    private static Descriptor? Parse(string sddl)
    {
        int dacl = sddl.IndexOf("D:", StringComparison.Ordinal);

        if (dacl < 0 || sddl.Contains("S:", StringComparison.Ordinal) || sddl.Contains("NO_ACCESS_CONTROL", StringComparison.Ordinal))
        {
            return null;
        }

        string head = sddl[..dacl];
        int groupAt = head.IndexOf("G:", StringComparison.Ordinal);
        string owner = groupAt < 0 ? head : head[..groupAt];
        string group = groupAt < 0 ? "" : head[groupAt..];

        string body = sddl[(dacl + 2)..];
        int first = body.IndexOf('(', StringComparison.Ordinal);
        string flags = first < 0 ? body : body[..first];

        if (flags.Contains(')', StringComparison.Ordinal))
        {
            return null;
        }

        List<string> aces = [];
        int index = first < 0 ? body.Length : first;

        while (index < body.Length)
        {
            if (body[index] != '(')
            {
                return null;
            }

            int close = body.IndexOf(')', index);

            if (close < 0 || body.IndexOf('(', index + 1) is int next && next >= 0 && next < close)
            {
                return null;
            }

            string ace = body[index..(close + 1)];

            if (ace.Trim('(', ')').Split(';').Length != 6)
            {
                return null;
            }

            aces.Add(ace);
            index = close + 1;
        }

        return aces.Count == 0 && flags.Length == 0 ? null : new Descriptor(owner, group, flags, aces);
    }

    private sealed record Descriptor(string Owner, string Group, string Flags, List<string> Aces)
    {
        public string Text() => $"{this.Owner}{this.Group}D:{this.Flags}{string.Concat(this.Aces)}";
    }
}
