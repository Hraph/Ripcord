using Ripcord.Adapters.Fake;
using Ripcord.Cli;
using Ripcord.Domain;
using Ripcord.Domain.Configuration;
using Ripcord.Ports.Configuration;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Cli;

/// `ripcord publish` by hand: once, and says how it went. The service runs the same thing
/// every fifteen seconds.
public class PublishCliTests
{
    private const string ConfigPath = @"C:\Program Files\Ripcord\ripcord.yaml";

    [Fact]
    public async Task A_host_that_reads_is_published_and_says_where()
    {
        CliRun run = await Run(Valid());

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains(@"C:\Program Files\Ripcord\state\state.json", run.Output, StringComparison.Ordinal);
        Assert.All(run.Output.ReplaceLineEndings("\n").Split('\n'), line => Assert.True(line.Length <= 75, line));
    }

    [Fact]
    public async Task Hyper_v_that_cannot_be_read_exits_three_with_the_reason()
    {
        CliRun run = await Run(Valid(), FakeHypervProvider.FailingLocally("WMI is down"));

        Assert.Equal(ExitCode.LocalAccessFailure, run.Code);
        Assert.Contains("nothing was published", run.Error, StringComparison.Ordinal);
        Assert.Contains("WMI is down", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_listener_publishes_nothing_and_is_not_a_failure()
    {
        ConfigurationDocumentStore store = new(document => document.Listener!.Enabled = false);

        CliRun run = await Run(store);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains("nothing to publish", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_configuration_that_does_not_load_exits_two() =>
        Assert.Equal(
            ExitCode.InvalidConfiguration,
            (await Run(new MemoryConfigStore(ConfigurationRead.Failed(ConfigPath, "no file")))).Code);

    private static ConfigurationDocumentStore Valid() => new(null);

    private static async Task<CliRun> Run(IConfigStore store, FakeHypervProvider? provider = null)
    {
        StringWriter output = new();
        StringWriter error = new();

        RipcordCli cli = new(
            TestPorts.With(store, provider),
            new CliEnvironment(FakeScenarios.LocalHostName, ConfigPath, @"C:\Program Files\Ripcord\ripcord.exe", null));

        ExitCode code = await cli.RunAsync(["publish"], output, error, CancellationToken.None);

        return new CliRun(code, output.ToString(), error.ToString());
    }

    private sealed class ConfigurationDocumentStore(Action<ConfigurationDocument>? adjust)
        : ReadOnlyConfigStore
    {
        public override ConfigurationRead Read(string path)
        {
            ConfigurationDocument document = ValidDocument.Create();
            adjust?.Invoke(document);
            return ConfigurationRead.Succeeded(document);
        }
    }

    private sealed record CliRun(ExitCode Code, string Output, string Error);
}
