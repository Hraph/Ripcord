using Ripcord.Adapters.Pairing;
using Ripcord.Adapters.Pairing.Transport;
using Ripcord.Adapters.Audit;
using Ripcord.Adapters.Dashboard;
using Ripcord.Adapters.Notify;
using Ripcord.Adapters.Update;
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

    /// The GitHub repository, by numeric id. Never `Hraph/Ripcord`: a rename leaves a redirect
    /// that HttpClient follows, and the day somebody recreates the abandoned name that URL
    /// stops failing and starts answering with a different repository's releases.
    private const long RepositoryId = 1_367_653_231;

    /// The public half of the key every release is signed with. Compiled in, never read from
    /// `ripcord.yaml`: an attacker who can edit the configuration must not be able to change
    /// what this host will accept as a genuine binary. Same reasoning as the numeric
    /// repository id above — the trust root is pinned at build time or it is not pinned.
    ///
    /// Rotating it means a release signed with the new key can only be installed by a binary
    /// that already carries it, so the changeover is one manual copy, once.
    private const string ReleaseSigningKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEepeb3PCHe0otFYoD2YDBS7FjGnlk
        hKtNTfhtSKbZ8GLsgKneO9F4/y8FpyoBjkWNwiZ73dKqs/0FXheRtBM9NA==
        -----END PUBLIC KEY-----
        """;

    /// Short enough that `check-update` on a host with no outbound access fails rather than
    /// hangs, which is the normal case on both of them.
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15);

    /// A release is tens of megabytes, so it gets its own budget rather than the one sized
    /// for a JSON answer.
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

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
            new RipcordPorts(
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
                new LoopbackDashboardServer(Console.Error.WriteLine),
                new JsonLinesAuditLog(
                    Path.Combine(AppContext.BaseDirectory, "audit.jsonl")),
                new TransportNotifier(
                    new EnvironmentSecretStore(), TransportNotifier.DefaultTimeout),
                new FileAlertStateStore(
                    Path.Combine(AppContext.BaseDirectory, "alert-state.json")),
                new GitHubReleaseFeed(
                    RepositoryId, $"ripcord/{BuildInfo.VersionWithCommit}", HttpTimeout),
                new HttpReleaseSource(
                    RepositoryId,
                    $"ripcord/{BuildInfo.VersionWithCommit}",
                    DownloadTimeout,
                    HttpReleaseSource.DefaultMaximumBytes),
                new FileBinarySwap(),
                clock),
            new CliEnvironment(
                Environment.MachineName,
                defaultConfigPath,
                binaryPath,
                Console.In,
                Environment.UserName,
                null,
                ReleaseSigningKey));

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
