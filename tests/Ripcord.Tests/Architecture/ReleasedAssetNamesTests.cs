using Ripcord.Domain.Updates;

namespace Ripcord.Tests.Architecture;

/// The file names the release workflow publishes, against the names a host looks for.
///
/// Two things now depend on those names being exactly right — `ripcord update`, which fetches
/// the binary and its signature, and `install.ps1`, which fetches those and the checksum. The
/// workflow writes them, and nothing compared the two. A rename there would break both at
/// once, silently, and only at the next release: the assets would publish under new names, the
/// metadata would list them, and every host would report a release that does not carry what it
/// needs. This is the comparison nobody was making.
public class ReleasedAssetNamesTests
{
    private static readonly string Workflow = Path.Combine(
        RepositoryLayout.Root, ".github", "workflows", "release.yml");

    /// The checksum is not in `ReleaseAssets.Required`, and deliberately: `ripcord update`
    /// verifies a signature and never reads it. The installer does, so it is asserted here
    /// rather than added to a list that would then claim an update needs it.
    private const string Checksum = "ripcord.exe.sha256";

    public static TheoryData<string> Published =>
        new([.. ReleaseAssets.Required, Checksum]);

    [Theory]
    [MemberData(nameof(Published))]
    public void The_release_publishes_every_asset_a_host_goes_looking_for(string asset)
    {
        Assert.Contains($"'publish/{asset}'", PublishCommand(), StringComparison.Ordinal);
    }

    /// Named where it is signed as well as where it is published: the signature is written
    /// under a name the workflow chooses, and it is the name `update` asks the API for.
    [Fact]
    public void The_signature_is_written_under_the_name_a_host_asks_for()
    {
        string workflow = File.ReadAllText(Workflow);

        Assert.Contains(
            $"-out publish/{ReleaseAssets.Signature} publish/{ReleaseAssets.Binary}",
            workflow,
            StringComparison.Ordinal);
    }

    /// The arguments `gh release create` is given, which is the whole of what a release page
    /// ends up carrying.
    private static string PublishCommand()
    {
        string[] lines = File.ReadAllLines(Workflow);

        int start = Array.FindIndex(
            lines, line => line.Contains("'release', 'create'", StringComparison.Ordinal));

        Assert.True(start >= 0, $"no `gh release create` invocation in {Workflow}");

        return string.Join(
            '\n', lines.Skip(start).TakeWhile(line => !line.Contains(')', StringComparison.Ordinal)));
    }
}
