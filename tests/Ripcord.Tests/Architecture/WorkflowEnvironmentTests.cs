namespace Ripcord.Tests.Architecture;

/// Environment variables the CI hands to a step, against the property names MSBuild reads out
/// of the environment.
///
/// This exists because a job called its variable `VERSION`, and Windows matches environment
/// variables without regard to case — so MSBuild picked it up as its own `Version` property
/// and stamped every `dotnet build` and `dotnet test` in that job with the release version.
/// It was invisible while the tree happened to declare the same number, and then it failed a
/// test whose whole job was to assert the tree declares none.
///
/// The collision costs nothing to avoid and is impossible to see in a diff, so it is checked
/// here rather than remembered. It reads the files as text on purpose: the test project takes
/// no YAML package, and the property is about the names, which a line is enough to find.
public class WorkflowEnvironmentTests
{
    /// Not every MSBuild property — the ones a workflow plausibly names, and every one of them
    /// changes what the produced binary says about itself.
    private static readonly string[] Reserved =
    [
        "Version", "VersionPrefix", "VersionSuffix", "PackageVersion",
        "AssemblyVersion", "FileVersion", "InformationalVersion",
        "Configuration", "Platform", "TargetFramework", "TargetFrameworks",
        "OutputPath", "BaseOutputPath", "AssemblyName", "RootNamespace",
    ];

    public static TheoryData<string> Workflows =>
        new(Directory
            .EnumerateFiles(
                Path.Combine(RepositoryLayout.Root, ".github", "workflows"), "*.yml")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order());

    [Theory]
    [MemberData(nameof(Workflows))]
    public void No_environment_variable_collides_with_a_property_msbuild_reads(string workflow)
    {
        string path = Path.Combine(RepositoryLayout.Root, ".github", "workflows", workflow);

        foreach (string name in EnvironmentNames(File.ReadAllLines(path)))
        {
            Assert.DoesNotContain(
                Reserved,
                reserved => string.Equals(reserved, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// The keys under every `env:` block, by indentation. Anything else in the file — a
    /// `with:` input, a step's own keys — is not handed to the process and cannot collide.
    private static IEnumerable<string> EnvironmentNames(string[] lines)
    {
        int block = -1;

        foreach (string line in lines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            int indent = line.Length - line.TrimStart().Length;

            if (block >= 0 && indent > block)
            {
                string entry = line.Trim();
                int colon = entry.IndexOf(':', StringComparison.Ordinal);

                if (colon > 0)
                {
                    yield return entry[..colon].Trim();
                }

                continue;
            }

            block = line.Trim() == "env:" ? indent : -1;
        }
    }
}
