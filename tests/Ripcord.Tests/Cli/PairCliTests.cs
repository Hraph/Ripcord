using Ripcord.Adapters.Fake;
using Ripcord.Cli;
using Ripcord.Domain;
using Ripcord.Domain.Configuration;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Cli;

/// `ripcord pair`: the line printed by `ripcord service` on the other host, pasted here, and
/// no hand-editing of `ripcord.yaml`. The decisions are covered in `ListenerPairingTests`.
public class PairCliTests
{
    private const string ConfigPath = "/opt/ripcord/ripcord.yaml";

    private const string TheirKey = "HV-PRIMARY-01:" + ValidDocument.PeerThumbprint;

    [Fact]
    public async Task Over_the_sample_it_writes_both_thumbprints_once_agreed()
    {
        MemoryConfigStore store = Sample();

        CliRun run = await Run(store, "y", TheirKey);

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Contains($"local_certificate_thumbprint: \"{ValidDocument.LocalThumbprint}\"", store.Written, StringComparison.Ordinal);
        Assert.Contains($"peer_certificate_thumbprint: \"{ValidDocument.PeerThumbprint}\"", store.Written, StringComparison.Ordinal);
        Assert.Equal(ConfigPath + ".1", store.Kept);

        // The line to carry back, and the restart the listener needs to read the file.
        Assert.Contains($"ripcord pair HV-REPLICA-01:{ValidDocument.LocalThumbprint}", run.Output, StringComparison.Ordinal);
        Assert.Contains("ripcord service restart", run.Output, StringComparison.Ordinal);
        Assert.All(run.Output.ReplaceLineEndings("\n").Split('\n'), line => Assert.True(line.Length <= 75, line));
    }

    [Fact]
    public async Task Enter_declines_and_nothing_is_written()
    {
        MemoryConfigStore store = Sample();

        CliRun run = await Run(store, "", TheirKey);

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Null(store.Written);
    }

    [Fact]
    public async Task Dry_run_asks_nothing_and_writes_nothing()
    {
        MemoryConfigStore store = Sample();

        CliRun run = await Run(store, "", TheirKey, "--dry-run");

        Assert.Equal(ExitCode.Success, run.Code);
        Assert.Null(store.Written);
        Assert.Contains("Nothing was changed", run.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("y/n", run.Output, StringComparison.Ordinal);
    }

    /// Pasted on the host it came from: said, and nothing touched.
    [Fact]
    public async Task This_hosts_own_key_is_refused()
    {
        MemoryConfigStore store = Sample();

        CliRun run = await Run(store, "y", $"HV-REPLICA-01:{ValidDocument.LocalThumbprint}");

        Assert.Equal(ExitCode.Refused, run.Code);
        Assert.Null(store.Written);
        Assert.Contains("own", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_key_says_where_to_get_one()
    {
        CliRun run = await Run(Sample(), "");

        Assert.Equal(ExitCode.InvalidConfiguration, run.Code);
        Assert.Contains("'ripcord service' prints on the other host", run.Error, StringComparison.Ordinal);
    }

    private static MemoryConfigStore Sample()
    {
        string text = Samples.Read("ripcord.dr.yaml");
        return new MemoryConfigStore(Yaml.Read(text), text);
    }

    private static async Task<CliRun> Run(MemoryConfigStore store, string typed, params string[] args)
    {
        StringWriter output = new();
        StringWriter error = new();

        RipcordCli cli = new(
            TestPorts.With(store),
            new CliEnvironment(
                FakeScenarios.LocalHostName, ConfigPath, "/opt/ripcord/ripcord", new StringReader(typed)));

        ExitCode code = await cli.RunAsync(["pair", .. args], output, error, CancellationToken.None);

        return new CliRun(code, output.ToString(), error.ToString());
    }

    private sealed record CliRun(ExitCode Code, string Output, string Error);
}
