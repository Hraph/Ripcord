using Ripcord.Adapters.Pairing;
using Ripcord.Adapters.Pairing.Transport;
using Ripcord.Adapters.Wmi;
using Ripcord.Adapters.Yaml;
using Ripcord.Cli;
using Ripcord.Domain.Replication;
using Ripcord.Domain;

namespace Ripcord.Host.Windows;

/// Composition root. The only place that knows about the WMI adapter, and the only project
/// that produces ripcord.exe.
internal static class Program
{
    /// Long enough to survive a busy host, short enough that a wedged WMI does not hang the
    /// command someone is running during an incident.
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(30);

    /// Resolved from the Windows store on every use rather than cached: a certificate renewed
    /// under a running service must be picked up without a restart.
    private static X509Certificate2 LocalCertificate() =>
        throw new NotImplementedException("wired with the deploy command");

    private static async Task<int> Main(string[] args)
    {
        // The configuration lives beside the binary: updating Ripcord is replacing the .exe.
        string defaultConfigPath = Path.Combine(AppContext.BaseDirectory, "ripcord.yaml");

        SystemClock clock = new();

        RipcordCli cli = new(
            new YamlConfigStore(),
            new WmiHypervProvider(Environment.MachineName, WmiTimeout),
            new MutualTlsPeerChannel(LocalCertificate, clock),
            new FileSnapshotStore(),
            clock,
            new CliEnvironment(Environment.MachineName, defaultConfigPath));

        using CancellationTokenSource cancellation = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        ExitCode code = await cli.RunAsync(
            args, Console.Out, Console.Error, cancellation.Token).ConfigureAwait(false);

        return (int)code;
    }
}
