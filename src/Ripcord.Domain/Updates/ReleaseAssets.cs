namespace Ripcord.Domain.Updates;

/// The files a release must publish for a host to be able to install it. The contract between
/// what the release workflow writes and what `ripcord update` will accept, stated once.
public static class ReleaseAssets
{
    public const string Binary = "ripcord.exe";

    public const string Signature = "ripcord.exe.sig";

    public static IReadOnlyList<string> Required { get; } = [Binary, Signature];

    /// What a release did not publish, in the order it should have. A release missing its
    /// signature is refused **by name**: it cannot be verified, and "could not be downloaded"
    /// would send somebody to look at the network instead of at the release.
    public static IReadOnlyList<string> MissingFrom(IEnumerable<string>? published)
    {
        HashSet<string> there = published is null
            ? []
            : new HashSet<string>(published, StringComparer.OrdinalIgnoreCase);

        return [.. Required.Where(asset => !there.Contains(asset))];
    }
}
