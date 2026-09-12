using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Ripcord.Tests;

/// Validates the toolchain itself: the solution filter the CI builds, and the commit hash the
/// audit log will depend on from milestone 4 (COHERENCE T4).
public class BuildChainTests
{
    private static readonly string[] WindowsOnlyProjects =
        ["Ripcord.Adapters.Wmi", "Ripcord.Host.Windows"];

    /// Asserting the exact set matters in both directions: a Windows project sneaking in breaks
    /// the Linux CI, and a Linux project left out is silently never built or tested.
    [Fact]
    public void Linux_solution_filter_covers_exactly_the_non_windows_projects()
    {
        using JsonDocument filter = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepositoryLayout.Root, "Ripcord.Linux.slnf")));

        string[] filtered = filter.RootElement
            .GetProperty("solution")
            .GetProperty("projects")
            .EnumerateArray()
            .Select(entry => Path.GetFileNameWithoutExtension(entry.GetString()!.Replace('\\', '/')))
            .Order()
            .ToArray();

        string[] expected = Directory
            .EnumerateFiles(RepositoryLayout.Root, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(name => !WindowsOnlyProjects.Contains(name))
            .Order()
            .ToArray();

        Assert.Equal(expected, filtered);
    }

    /// Checks the stamping logic, not the checkout: a build with no repository must still
    /// produce a "+<revision>" part, so a source tarball does not turn the CI red.
    [Fact]
    public void Build_stamps_a_revision_into_the_informational_version()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+\+([0-9a-f]{12}|unknown)$", Cli.BuildInfo.VersionWithCommit);
    }

    [Fact]
    public void Version_matches_the_version_prefix_in_the_build_properties()
    {
        XDocument properties =
            XDocument.Load(Path.Combine(RepositoryLayout.Root, "Directory.Build.props"));

        string versionPrefix = properties.Descendants()
            .Single(element => element.Name.LocalName == "VersionPrefix")
            .Value;

        Assert.Equal(versionPrefix, Cli.BuildInfo.Version);
    }

    [Fact]
    public void Commit_hash_is_reported_separately_from_the_version()
    {
        Assert.DoesNotContain('+', Cli.BuildInfo.Version);
        Assert.Matches("^([0-9a-f]{12}|unknown)$", Cli.BuildInfo.CommitHash);
    }
}
