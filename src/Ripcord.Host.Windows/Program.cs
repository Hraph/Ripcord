using System.Security.Cryptography.X509Certificates;
using Ripcord.Adapters.Pairing;
using Ripcord.Adapters.Pairing.Transport;
using Ripcord.Adapters.Wmi;
using Ripcord.Adapters.Wmi.Deployment;
using Ripcord.Adapters.Yaml;
using Ripcord.Cli;
using Ripcord.Domain;

namespace Ripcord.Host.Windows;

/// Composition root. The only place that knows about the WMI adapter, and the only project
/// that produces ripcord.exe.
internal static class Program
{
    /// Long enough to survive a busy host, short enough that a wedged WMI does not hang the
    /// command someone is running during an incident.
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(30);

    private static async Task<int> Main(string[] args)
    {
        // The configuration lives beside the binary: updating Ripcord is replacing the .exe.
        string binaryPath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "ripcord.exe");

        string defaultConfigPath = Path.Combine(AppContext.BaseDirectory, "ripcord.yaml");

        SystemClock clock = new();
        FileSnapshotStore snapshotStore = new();

        RipcordCli cli = new(
            new YamlConfigStore(),
            new WmiHypervProvider(Environment.MachineName, WmiTimeout),
            new MutualTlsPeerChannel(WindowsCertificates.WithThumbprint, PeerTrust.MachineStore, clock),
            snapshotStore,
            new WindowsDeploymentExecutor(),
            new LoopingPeerListener(
                WindowsCertificates.WithThumbprint, PeerTrust.MachineStore, snapshotStore, clock),
            clock,
            new CliEnvironment(
                Environment.MachineName, defaultConfigPath, binaryPath, Console.In));

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

/// Finds a certificate in the Windows store by thumbprint. The thumbprint identifies
/// (decision D6): a subject lookup can return several certificates, including an expired one,
/// and pick the wrong one.
///
/// Resolved on every use rather than cached, so a certificate renewed under a running service
/// is picked up without a restart.
internal static class WindowsCertificates
{
    public static X509Certificate2 WithThumbprint(string thumbprint)
    {
        // LocalMachine\My: the service account has no user store of its own.
        using X509Store store = new(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);

        foreach (X509Certificate2 candidate in store.Certificates)
        {
            if (string.Equals(candidate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)
                && candidate.HasPrivateKey)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"no certificate with thumbprint {thumbprint} and a private key in LocalMachine\\My");
    }
}
