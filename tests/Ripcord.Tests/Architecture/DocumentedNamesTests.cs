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

    /// `Stop-Service <names>` and `Start-Service <names>`, wherever the documentation spells one
    /// out, must name the services the deployment actually creates — both of them: both run
    /// `ripcord.exe`, and a procedure that stops one leaves the file held by the other.
    [Theory]
    [MemberData(nameof(All))]
    public void Every_documented_service_command_names_the_service_that_is_installed(string document)
    {
        string path = Path.Combine(RepositoryLayout.Root, document);

        if (!File.Exists(path))
        {
            return;
        }

        string[] installed = [.. RipcordService.All.Select(service => service.Name)];

        MatchCollection commands = Regex.Matches(
            File.ReadAllText(path), @"(?:Stop|Start)-Service\s+((?:[A-Za-z0-9._-]+\s*,\s*)*[A-Za-z0-9._-]+)");

        // The update procedure stops and starts the services: reworded out of the pattern, this
        // test would pass on nothing at all (D72).
        if (document == Path.Combine("docs", "RELEASING.md"))
        {
            Assert.NotEmpty(commands);
        }

        foreach (Match command in commands)
        {
            string[] named = [.. command.Groups[1].Value.Split(',').Select(name => name.Trim())];

            Assert.All(named, name => Assert.Contains(name, installed, StringComparer.OrdinalIgnoreCase));
            Assert.Equal(installed.Order(StringComparer.Ordinal), named.Order(StringComparer.Ordinal));
        }
    }

    /// The event log commands the service page shows are the ones `ripcord service` prints,
    /// which name the source `service install` registers.
    [Fact]
    public void The_documented_event_log_commands_are_the_printed_ones()
    {
        string text = File.ReadAllText(
            Path.Combine(RepositoryLayout.Root, "docs", "commands", "service.md"));

        string[] commands =
        [
            .. text.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("Get-WinEvent", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal),
        ];

        Assert.Equal(ServiceDiagnosis.EventLogCommands.Count, commands.Length);
        Assert.All(commands, command => Assert.Contains(command, ServiceDiagnosis.EventLogCommands));
    }
}
