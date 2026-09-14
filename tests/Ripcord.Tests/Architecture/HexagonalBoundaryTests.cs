using System.Xml.Linq;

namespace Ripcord.Tests.Architecture;

/// The hexagonal boundary is a test, not an intention. These assertions encode the project
/// table in docs/milestones/milestone-0.md; changing the architecture means changing them
/// deliberately rather than by accident.
///
/// They parse the .csproj files rather than reflecting over assemblies: an unused
/// PackageReference is invisible to reflection, and an unused reference today is a used one
/// next week.
public class HexagonalBoundaryTests
{
    /// Item types that can pull code into a project. All of them are checked, not just
    /// ProjectReference and PackageReference.
    private static readonly string[] ReferenceItemNames =
        ["ProjectReference", "PackageReference", "Reference", "FrameworkReference", "COMReference"];

    private static readonly Dictionary<string, string[]> AllowedProjectReferences = new()
    {
        ["Ripcord.Domain"] = [],
        ["Ripcord.Ports"] = ["Ripcord.Domain"],
        ["Ripcord.Application"] = ["Ripcord.Domain", "Ripcord.Ports"],
        ["Ripcord.Cli"] = ["Ripcord.Domain", "Ripcord.Ports", "Ripcord.Application"],
        ["Ripcord.Adapters.Fake"] = ["Ripcord.Domain", "Ripcord.Ports"],
        ["Ripcord.Adapters.Yaml"] = ["Ripcord.Domain", "Ripcord.Ports"],
        ["Ripcord.Adapters.Pairing"] = ["Ripcord.Domain", "Ripcord.Ports"],
        ["Ripcord.Adapters.Notify"] = ["Ripcord.Domain", "Ripcord.Ports"],
        ["Ripcord.Adapters.Audit"] = ["Ripcord.Domain", "Ripcord.Ports"],
        ["Ripcord.Adapters.Dashboard"] = ["Ripcord.Domain", "Ripcord.Ports"],
        ["Ripcord.Adapters.Update"] = ["Ripcord.Domain", "Ripcord.Ports"],
        ["Ripcord.Adapters.Wmi"] = ["Ripcord.Domain", "Ripcord.Ports"],
        ["Ripcord.Host.Windows"] =
        [
            "Ripcord.Cli", "Ripcord.Adapters.Wmi", "Ripcord.Adapters.Yaml",
            "Ripcord.Adapters.Pairing", "Ripcord.Adapters.Audit", "Ripcord.Adapters.Notify",
            "Ripcord.Adapters.Dashboard", "Ripcord.Adapters.Update",
        ],
        ["Ripcord.Tests"] =
        [
            "Ripcord.Domain", "Ripcord.Ports", "Ripcord.Application",
            "Ripcord.Cli", "Ripcord.Adapters.Fake", "Ripcord.Adapters.Yaml",
            "Ripcord.Adapters.Pairing", "Ripcord.Adapters.Audit", "Ripcord.Adapters.Notify",
            "Ripcord.Adapters.Dashboard", "Ripcord.Adapters.Update",
        ],
    };

    /// Packages each project may take. Everything absent from this map may take none —
    /// a NuGet dependency in the Domain is the leak this whole table exists to prevent.
    private static readonly Dictionary<string, string[]> AllowedPackages = new()
    {
        // The service control manager handshake, and nothing else it brings with it. Named in
        // milestone 1b as belonging to the composition root only: a Domain that could host is
        // a Domain that could listen.
        ["Ripcord.Host.Windows"] = ["Microsoft.Extensions.Hosting.WindowsServices"],

        ["Ripcord.Adapters.Wmi"] = ["Microsoft.Management.Infrastructure"],
        ["Ripcord.Adapters.Yaml"] = ["YamlDotNet"],
        ["Ripcord.Tests"] =
            ["coverlet.collector", "Microsoft.NET.Test.Sdk", "xunit", "xunit.runner.visualstudio"],
    };

    /// Only these two may target Windows. Everything else must run in the Linux container.
    private static readonly string[] WindowsOnlyProjects =
        ["Ripcord.Adapters.Wmi", "Ripcord.Host.Windows"];

    public static TheoryData<string> AllProjects => new(AllowedProjectReferences.Keys);

    /// Without this, a project added tomorrow is simply not covered by any assertion below.
    [Fact]
    public void Every_project_in_the_repository_is_covered_by_the_matrix()
    {
        string[] onDisk =
        [
            .. Directory.EnumerateFiles(Path.Combine(RepositoryLayout.Root, "src"), "*.csproj",
                SearchOption.AllDirectories),
            .. Directory.EnumerateFiles(Path.Combine(RepositoryLayout.Root, "tests"), "*.csproj",
                SearchOption.AllDirectories),
        ];

        Assert.Equal(
            AllowedProjectReferences.Keys.Order(),
            onDisk.Select(Path.GetFileNameWithoutExtension).OfType<string>().Order());
    }

    [Fact]
    public void Domain_references_nothing_at_all()
    {
        XDocument project = Load("Ripcord.Domain");

        foreach (string itemName in ReferenceItemNames)
        {
            Assert.Empty(References(project, itemName));
        }
    }

    /// Directory.Build.props applies to every project, so a reference item there lands in the
    /// Domain without touching Ripcord.Domain.csproj at all.
    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("Directory.Packages.props")]
    public void Repository_wide_build_files_inject_no_references(string fileName)
    {
        string path = Path.Combine(RepositoryLayout.Root, fileName);
        if (!File.Exists(path))
        {
            return;
        }

        XDocument buildFile = XDocument.Load(path);

        foreach (string itemName in ReferenceItemNames.Append("Using"))
        {
            Assert.Empty(References(buildFile, itemName));
        }
    }

    [Theory]
    [MemberData(nameof(AllProjects))]
    public void Project_references_only_what_the_architecture_allows(string projectName)
    {
        string[] actual = References(Load(projectName), "ProjectReference")
            .Select(ProjectNameOf)
            .Order()
            .ToArray();

        Assert.Equal(AllowedProjectReferences[projectName].Order(), actual);
    }

    [Theory]
    [MemberData(nameof(AllProjects))]
    public void Project_takes_no_package_it_is_not_allowed(string projectName)
    {
        string[] allowed = AllowedPackages.GetValueOrDefault(projectName, []);
        string[] actual = References(Load(projectName), "PackageReference").Order().ToArray();

        Assert.Empty(actual.Except(allowed));
    }

    [Theory]
    [MemberData(nameof(AllProjects))]
    public void Only_the_windows_projects_target_windows(string projectName)
    {
        string[] frameworks = Values(Load(projectName), "TargetFramework", "TargetFrameworks");

        if (WindowsOnlyProjects.Contains(projectName))
        {
            Assert.Equal(["net10.0-windows"], frameworks);
        }
        else
        {
            // Inherits net10.0 from Directory.Build.props; overriding it would be the leak.
            Assert.Empty(frameworks);
        }
    }

    [Theory]
    [MemberData(nameof(AllProjects))]
    public void Only_the_windows_host_is_an_executable(string projectName)
    {
        string[] expected = projectName switch
        {
            "Ripcord.Host.Windows" => ["Exe"],
            "Ripcord.Cli" => ["Library"],   // stated explicitly: the obvious reading is wrong
            _ => [],
        };

        Assert.Equal(expected, Values(Load(projectName), "OutputType"));
    }

    private static XDocument Load(string projectName) =>
        XDocument.Load(RepositoryLayout.ProjectFile(projectName));

    /// MSBuild paths use backslashes whatever the OS, and Path.GetFileNameWithoutExtension
    /// does not split on them off Windows — which would pass locally and fail in the CI.
    private static string ProjectNameOf(string includePath) =>
        Path.GetFileNameWithoutExtension(includePath.Replace('\\', '/'));

    private static string[] References(XDocument project, string itemName) =>
        project.Descendants()
            .Where(element => element.Name.LocalName == itemName)
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

    private static string[] Values(XDocument project, params string[] elementNames) =>
        project.Descendants()
            .Where(element => elementNames.Contains(element.Name.LocalName))
            .Select(element => element.Value)
            .ToArray();
}
