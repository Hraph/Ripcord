using System.Text.RegularExpressions;

namespace Ripcord.Domain.Deployment;

/// Reading what `net localgroup` answers. The adapter runs it; what the answer means is here.
///
/// The header and the closing line are in the host's language, so neither is read: members are
/// the lines after the dashed rule, compared whole — `NT SERVICE\ripcord` must never be found
/// in `NT SERVICE\ripcord-publish`.
public static partial class LocalGroup
{
    /// Hyper-V Administrators, by its well-known SID: its name is translated on a French host.
    public const string HyperVAdministratorsSid = "S-1-5-32-578";

    /// "System error 1378" / "Erreur système 1378": already a member.
    private const int AlreadyMember = 1378;

    /// 1377: not a member.
    private const int NotMember = 1377;

    public static bool HasMember(string? output, string account)
    {
        ArgumentNullException.ThrowIfNull(account);

        bool members = false;

        foreach (string raw in (output ?? "").Split('\n'))
        {
            string line = raw.Trim();

            if (!members)
            {
                members = line.Length > 0 && line.All(character => character == '-');
                continue;
            }

            if (string.Equals(line, account, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// Whether a failed add or remove asked for what is already so. The number is the same in
    /// every language; `net` exits 2 either way.
    public static bool LeavesNothingToDo(bool adding, string? output) =>
        SystemError().Match(output ?? "") is { Success: true } match
        && int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture)
            == (adding ? AlreadyMember : NotMember);

    [GeneratedRegex(@"\b137[78]\b")]
    private static partial Regex SystemError();
}
