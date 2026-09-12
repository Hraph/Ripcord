namespace Ripcord.Tests;

/// Locates the repository on disk. The boundary tests read .csproj files rather than
/// compiled assemblies, so they need real paths (COHERENCE B2/B3).
internal static class RepositoryLayout
{
    public static string Root { get; } = FindRoot();

    public static string ProjectFile(string projectName) =>
        projectName == "Ripcord.Tests"
            ? Path.Combine(Root, "tests", projectName, projectName + ".csproj")
            : Path.Combine(Root, "src", projectName, projectName + ".csproj");

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ripcord.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                $"Could not find Ripcord.sln above {AppContext.BaseDirectory}.");
    }
}
