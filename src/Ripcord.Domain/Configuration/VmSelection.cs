using System.Globalization;

namespace Ripcord.Domain.Configuration;

/// What was picked at the VM question: the host's spelling of each name, in the order typed,
/// or why the answer was not taken.
public sealed record VmChoice(IReadOnlyList<string> Names, string? Refusal)
{
    public static VmChoice Refused(string refusal) => new([], refusal);

    public bool Equals(VmChoice? other) =>
        other is not null && this.Refusal == other.Refusal && this.Names.SequenceEqual(other.Names);

    public override int GetHashCode() => HashCode.Combine(this.Refusal, this.Names.Count);
}

/// The VM question's default and its parser, kept side by side because they must round-trip:
/// Enter on the default has to give back the file's VMs still on this host, in the file's
/// order — which is the start order within a priority.
public static class VmSelection
{
    public const string All = "all";

    /// `all` when there is nothing to keep or the file already has every VM in the list's
    /// order; otherwise the file's VMs as their numbers in the list. Numbers, not names: they
    /// are what the prompt asks for, and names overflow the console line. Null with no list.
    public static string? Default(IReadOnlyList<string> offered, IReadOnlyList<string> seeded)
    {
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(seeded);

        if (offered.Count == 0)
        {
            return null;
        }

        List<int> kept = [];

        foreach (string name in seeded)
        {
            int index = IndexOf(offered, name);

            if (index >= 0 && !kept.Contains(index))
            {
                kept.Add(index);
            }
        }

        if (kept.Count == 0 || kept.SequenceEqual(Enumerable.Range(0, offered.Count)))
        {
            return All;
        }

        return string.Join(",", kept.Select(index => (index + 1).ToString(CultureInfo.InvariantCulture)));
    }

    /// Comma-separated numbers from the list or names, or `all` on its own. A number within
    /// the list is read as its number first, so a VM named `2019` is still reachable by name
    /// on a host with fewer VMs. With no list, a name is taken as typed.
    public static VmChoice Parse(string typed, IReadOnlyList<string> offered)
    {
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(offered);

        string[] tokens = typed.Split(
            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (tokens.Any(token => string.Equals(token, All, StringComparison.OrdinalIgnoreCase)))
        {
            if (tokens.Length > 1)
            {
                return VmChoice.Refused("'all' stands alone.");
            }

            return offered.Count > 0
                ? new VmChoice([.. offered], null)
                : VmChoice.Refused("this host's VMs could not be read, so name them instead of 'all'.");
        }

        List<string> chosen = [];

        foreach (string token in tokens)
        {
            if (Resolve(token, offered) is not { } name)
            {
                return VmChoice.Refused($"there is no VM {token} in the list.");
            }

            if (!chosen.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                chosen.Add(name);
            }
        }

        return chosen.Count > 0 ? new VmChoice(chosen, null) : VmChoice.Refused("name at least one VM.");
    }

    private static string? Resolve(string token, IReadOnlyList<string> offered)
    {
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number >= 1 && number <= offered.Count)
        {
            return offered[number - 1];
        }

        int index = IndexOf(offered, token);

        if (index >= 0)
        {
            return offered[index];
        }

        return offered.Count == 0 ? token : null;
    }

    private static int IndexOf(IReadOnlyList<string> offered, string name)
    {
        for (int index = 0; index < offered.Count; index++)
        {
            if (string.Equals(offered[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
