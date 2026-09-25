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
