namespace Ripcord.Domain.Deployment;

/// Reading `icacls` output. The adapter runs the command; what its answer means is decided
/// here, where a test can reach it.
///
/// The question is narrow: does this account have read access to this file? A substring search
/// for the account name answers it wrongly in the one case that matters — an entry denying the
/// account is also an entry naming it, and a deployment that skipped granting access because
/// it found the word in a `(DENY)` line would leave the listener unable to read the snapshot
/// it exists to serve.
public static class AccessControl
{
    /// Rights that include reading a file. `(GR)` is the generic form, `(RX)`, `(M)` and `(F)`
    /// all contain it, and `(RD)` is the explicit one; anything else is not read access.
    private static readonly string[] ReadRights = ["R", "RX", "M", "F", "GR", "RD", "GA"];

    private const string Deny = "(DENY)";

    /// Rights that include modifying, and so deleting, a file. The listener's log folder needs
    /// this rather than write alone: pruning an old log deletes it.
    private static readonly string[] ModifyRights = ["M", "F", "GA"];

    /// Deny wins, as it does in Windows itself. An account named nowhere in the output has no
    /// access, which is the same answer as no output at all — the caller treats both as "grant
    /// it", and granting access that already exists changes nothing.
    public static bool GrantsRead(string? icaclsOutput, string account) =>
        Grants(icaclsOutput, account, ReadRights);

    public static bool GrantsModify(string? icaclsOutput, string account) =>
        Grants(icaclsOutput, account, ModifyRights);

    /// An entry of its own for this account, not one inherited from a parent: only those were
    /// granted by a deployment, and only those can be taken back where they stand.
    public static bool GrantsExplicitly(string? icaclsOutput, string account)
    {
        ArgumentNullException.ThrowIfNull(account);

        return (icaclsOutput ?? "").Split('\n').Any(line =>
            Entry(line, account) is { } rights
            && !rights.Contains(Deny, StringComparison.OrdinalIgnoreCase)
            && !rights.Contains("(I)", StringComparison.OrdinalIgnoreCase));
    }

    /// `icacls "<folder>\*"` lists every file, each block opening at column 0 with the path as
    /// `<folder>\<name>`. The files that give this account an entry of its own. Only a block
    /// under `folder` counts, so a path printed in another form is left out rather than taken
    /// for another file; a key file name holds no space and no colon.
    public static IReadOnlyList<string> FilesGrantingExplicitly(
        string? icaclsOutput, string folder, string account)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(account);

        string prefix = folder.TrimEnd('\\') + "\\";
        List<string> files = [];
        string? current = null;

        foreach (string raw in (icaclsOutput ?? "").Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.Length > 0 && !char.IsWhiteSpace(line[0]))
            {
                // Any other line at column 0 ends the block: an error, the summary.
                current = null;

                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    int end = line.IndexOfAny([' ', ':'], prefix.Length);
                    string name = end < 0 ? line[prefix.Length..] : line[prefix.Length..end];
                    current = name.Length > 0 ? prefix + name : null;
                }
            }

            if (current is not null && !files.Contains(current) && GrantsExplicitly(line, account))
            {
                files.Add(current);
            }
        }

        return files;
    }

    private static bool Grants(string? icaclsOutput, string account, string[] wanted)
    {
        ArgumentNullException.ThrowIfNull(account);

        bool granted = false;

        foreach (string line in (icaclsOutput ?? "").Split('\n'))
        {
            if (Entry(line, account) is not { } rights)
            {
                continue;
            }

            if (rights.Contains(Deny, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            granted |= Rights(rights).Any(right =>
                wanted.Contains(right, StringComparer.OrdinalIgnoreCase));
        }

        return granted;
    }

    /// One access control entry for this account, as the rights part of the line, or null when
    /// the line is about somebody else. The file name precedes the first entry on the first
    /// line, so the account is matched anywhere before its colon rather than at the start.
    private static string? Entry(string line, string account)
    {
        int at = line.IndexOf(account, StringComparison.OrdinalIgnoreCase);

        if (at < 0)
        {
            return null;
        }

        string rest = line[(at + account.Length)..].TrimStart();

        return rest.StartsWith(':') ? rest[1..].Trim() : null;
    }

    /// `(R)`, `(RX)`, or the comma-separated explicit form `(RD,REA,X)`. Split into the tokens
    /// inside the brackets so a right is matched whole: `(W)` must never satisfy a search for
    /// a right whose letter it happens to contain.
    private static IEnumerable<string> Rights(string rights) =>
        rights
            .Split(['(', ')', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim());
}
