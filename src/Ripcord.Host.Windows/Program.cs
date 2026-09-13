using Ripcord.Adapters.Pairing;
using Ripcord.Adapters.Pairing.Transport;
using Ripcord.Adapters.Audit;
using Ripcord.Adapters.Wmi;
using Ripcord.Adapters.Wmi.Deployment;
using Ripcord.Adapters.Yaml;
using Ripcord.Cli;
using Ripcord.Domain;
using Ripcord.Domain.Pairing;

namespace Ripcord.Host.Windows;

/// Composition root. The only place that knows about the WMI adapter, and the only project
/// that produces ripcord.exe.
internal static class Program
{
    /// Long enough to survive a busy host, short enough that a wedged WMI does not hang the
    /// command someone is running during an incident.
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(30);

    /// Never silent: every connection the listener handles says who called and what was
    /// decided. `ripcord serve` is run by hand to verify the exit criterion, and a refusal
    /// nobody can see is a refusal nobody can trust.
    private static void Report(ServedConnection connection) =>
        Console.Error.WriteLine(
            $"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} {connection.RemoteAddress} "
            + $"{(connection.Served ? "served" : "refused")}: {connection.Verdict.Reason()}");

    private static async Task<int> Main(string[] args)
    {
        // The configuration lives beside the binary: updating Ripcord is replacing the .exe.
        string binaryPath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "ripcord.exe");

        string defaultConfigPath = Path.Combine(AppContext.BaseDirectory, "ripcord.yaml");

        SystemClock clock = new();
        FileSnapshotStore snapshotStore = new();
        MachineCertificateStore certificates = new();

        RipcordCli cli = new(
            new YamlConfigStore(),
            new WmiHypervProvider(Environment.MachineName, WmiTimeout),
            new WmiHostSystemProvider(WmiTimeout),
            certificates,
            new MutualTlsPeerChannel(
                MachineCertificateStore.WithPrivateKey, PeerTrust.MachineStore, clock),
            snapshotStore,
            new WindowsDeploymentExecutor(),
            new LoopingPeerListener(
                MachineCertificateStore.WithPrivateKey,
                PeerTrust.MachineStore,
                snapshotStore,
                clock,
                Report),
            new JsonLinesAuditLog(
                Path.Combine(AppContext.BaseDirectory, "audit.jsonl")),
            clock,
            new CliEnvironment(
                Environment.MachineName,
                defaultConfigPath,
                binaryPath,
                Console.In,
                Environment.UserName));

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
