using System.Text.RegularExpressions;
using Ripcord.Adapters.Yaml;
using Ripcord.Domain.Configuration;
using Ripcord.Ports.Configuration;
using Ripcord.Tests.Architecture;

namespace Ripcord.Tests.Configuration;

/// The configuration `install.ps1` writes when a host has no sample to copy from — read here,
/// out of the script itself, and put through the validator the binary uses.
///
/// The installer cannot run on the machine this suite runs on, and the file it writes is the
/// first thing an operator opens. So the two halves are checked against each other rather than
/// trusted to agree: the template must parse, and it must be **refused** for exactly the fields
/// only the operator can answer.
///
/// A default configuration that loaded cleanly would be the worse outcome. It would describe a
/// pair that does not exist, and `ripcord status` would answer confidently about the wrong
/// peer.
public class DefaultConfigurationTests
{
    private const string NodeName = "HV-REPLICA-01";

    /// The fields the machine cannot know. Each one is blank in the template, and each one is
    /// a refusal with the path in it — which is what turns "it does not validate" into a list
    /// somebody can work through.
    private static readonly string[] OperatorMustAnswer =
    [
        "peer.hostname",
        "peer.address",
        "replication.expected_switch_name",
        "vms",
    ];

    [Theory]
    [InlineData("primary")]
    [InlineData("dr")]
    public void The_template_is_refused_for_the_fields_only_the_operator_knows(string role)
    {
        ConfigurationValidation validation = Validate(role);

        Assert.Null(validation.Configuration);

        Assert.Equal(
            OperatorMustAnswer.Order(),
            validation.Errors.Select(error => error.Path).Distinct().Order());
    }

    /// Blank fields, not missing sections: the operator opens the file and sees where the
    /// answers go. A section left out entirely is reported as one error naming the section,
    /// and that is a worse checklist.
    [Fact]
    public void The_template_is_readable_yaml_naming_this_host()
    {
        ConfigurationRead read = Read(Template("dr"));

        Assert.Empty(read.Errors);
        Assert.Equal(NodeName, read.Document!.Node!.Hostname);
    }

    /// The role is the one thing the installer asks for, and the two files of a pair are
    /// mirror images — writing `primary` into the DR host's file would invert every
    /// cross-host rule at once.
    [Theory]
    [InlineData("primary", "primary")]
    [InlineData("dr", "replica")]
    public void The_role_the_installer_was_given_is_the_role_it_writes(
        string chosen, string expected)
    {
        ConfigurationRead read = Read(Template(chosen));

        Assert.Equal(expected, read.Document!.Replication!.ExpectedRole);
    }

    private static ConfigurationValidation Validate(string role) =>
        ConfigurationValidator.Validate(Read(Template(role)).Document, NodeName);

    private static ConfigurationRead Read(string yaml)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");

        try
        {
            File.WriteAllText(path, yaml);
            return new YamlConfigStore().Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// The here-string out of the installer, with the two substitutions the script itself
    /// makes. Read from the file rather than copied here, because a copy is what drifts.
    private static string Template(string role)
    {
        string script = File.ReadAllText(Path.Combine(RepositoryLayout.Root, "install.ps1"));

        Match template = Regex.Match(
            script,
            @"\$script:DefaultConfiguration = @'\r?\n(.*?)\r?\n'@",
            RegexOptions.Singleline);

        Assert.True(template.Success, "install.ps1 no longer carries a default configuration");

        return template.Groups[1].Value
            .Replace("__NODE__", NodeName, StringComparison.Ordinal)
            .Replace("__ROLE__", role == "dr" ? "replica" : "primary", StringComparison.Ordinal);
    }
}
