using System.Text.RegularExpressions;
using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Architecture;

/// Names the documentation hands an operator to type. They are read on the day of an update,
/// on a host that is about to be failed over, and a name that is merely plausible fails there
/// rather than here.
///
/// This exists because `docs/RELEASING.md` told the operator to `Stop-Service ripcord-listener`
/// for four steps of a procedure, while the deployment plan has always created a service called
/// `ripcord`. Every step of that procedure would have failed on the name, and the one after it
/// replaces a file the running service is holding open.
public class DocumentedNamesTests
{
    private static readonly string[] Documents =
    [
        "README.md",
        "SECURITY.md",
        Path.Combine("docs", "RELEASING.md"),
        Path.Combine("docs", "RELEASE_NOTES.md"),
    ];

    public static TheoryData<string> All => new(Documents);

    /// `Stop-Service <name>` and `Start-Service <name>`, wherever the documentation spells one
    /// out, must name the service the deployment actually creates.
    [Theory]
    [MemberData(nameof(All))]
    public void Every_documented_service_command_names_the_service_that_is_installed(string document)
    {
        string path = Path.Combine(RepositoryLayout.Root, document);

        if (!File.Exists(path))
        {
            return;
        }

        string[] named =
        [
            .. Regex
                .Matches(File.ReadAllText(path), @"(?:Stop|Start)-Service\s+([A-Za-z0-9._-]+)")
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        Assert.All(
            named,
            name => Assert.Equal(DeploymentPlan.ServiceName, name, ignoreCase: true));
    }

    /// The event log source the service page tells an operator to filter on is the one
    /// `service install` registers; the runtime's own source is the only other one named.
    [Fact]
    public void The_documented_event_source_is_the_one_that_is_registered()
    {
        string text = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "docs", "commands", "service.md"));

        string[] sources =
        [
            .. Regex
                .Matches(text, @"ProviderName='([^']+)'")
                .Select(match => match.Groups[1].Value)
                .Where(source => source != ".NET Runtime"),
        ];

        Assert.NotEmpty(sources);
        Assert.All(sources, source => Assert.Equal(DeploymentPlan.EventSource, source));
    }
}
