using System.Reflection;

namespace Ripcord.Cli;

/// Build identity, read from the assembly attribute the SDK stamps at compile time.
public static class BuildInfo
{
    private static readonly (string Version, string Commit) Parsed = Parse(
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion);

    /// Semantic version without the revision, as "0.1.0".
    public static string Version => Parsed.Version;

    /// Short commit hash, or "unknown" when built outside a repository.
    public static string CommitHash => Parsed.Commit;

    /// Both parts, as the SDK wrote them: "0.1.0+abc123def456".
    public static string VersionWithCommit => $"{Parsed.Version}+{Parsed.Commit}";

    /// The separator is SemVer build metadata, which the SDK appends from SourceRevisionId.
    private static (string Version, string Commit) Parse(string? informationalVersion)
    {
        if (string.IsNullOrEmpty(informationalVersion))
        {
            return ("unknown", "unknown");
        }

        int separator = informationalVersion.IndexOf('+');
        return separator < 0
            ? (informationalVersion, "unknown")
            : (informationalVersion[..separator], informationalVersion[(separator + 1)..]);
    }
}
