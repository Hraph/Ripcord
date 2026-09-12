namespace Ripcord.Domain.Inventory;

/// MAC addresses reach Ripcord in three spellings — `00155D010203`, `00-15-5D-01-02-03` and
/// `00:15:5D:01:02:03` — depending on the CIM property and on who typed it. They name the same
/// adapter, so comparison normalises rather than refuses.
public static class MacAddress
{
    /// A VM configured for a dynamic MAC reports all zeroes until it has been started once,
    /// which is not an address and must never compare equal to another VM's.
    private const string Unassigned = "000000000000";

    public static bool Known(string? value) => Normalise(value) is not null;

    /// Unknown on either side is neither a match nor a mismatch. The caller has to ask
    /// `Known` first; answering "not equal" to an unanswerable question is how a rule
    /// concludes a fault that was never observed.
    public static bool Same(string? left, string? right) =>
        Normalise(left) is { } first && Normalise(right) is { } second && first == second;

    public static string? Normalise(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string stripped = string.Concat(value.Where(Uri.IsHexDigit)).ToUpperInvariant();

        return stripped.Length == 12 && stripped != Unassigned ? stripped : null;
    }
}
