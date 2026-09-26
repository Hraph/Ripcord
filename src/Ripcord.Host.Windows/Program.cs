using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using Ripcord.Adapters.Pairing;
using Ripcord.Adapters.Pairing.Transport;
using Ripcord.Adapters.Audit;
using Ripcord.Adapters.Dashboard;
using Ripcord.Adapters.Diagnostics;
using Ripcord.Adapters.Notify;
using Ripcord.Adapters.Update;
using Ripcord.Adapters.Wmi;
using Ripcord.Adapters.Wmi.Deployment;
using Ripcord.Adapters.Yaml;
using Ripcord.Cli;
using Ripcord.Cli.Rendering;
using Ripcord.Domain;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Diagnostics;
using Ripcord.Domain.Pairing;
using Ripcord.Ports.Diagnostics;

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
    /// nobody can see is a refusal nobody can trust. As a service there is no console, so the
    /// line goes to the listener log instead.
    private static void Report(
        bool asService, FileDiagnosticLog diagnostics, ServedConnection connection)
    {
        string line = $"{connection.RemoteAddress} "
            + $"{(connection.Served ? "served" : "refused")}: {connection.Verdict.Reason()}";

        if (asService)
        {
            diagnostics.Write(DiagnosticEntry.Of("listener", line));
        }
        else
        {
            Console.Error.WriteLine($"{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss} {line}");
        }
    }

    /// Whether a rendered block may carry colour, decided once, here — and **per stream**.
    ///
    /// The two are decided separately because they are redirected separately: `ripcord check
    /// 2> errors.log` on a live console leaves stdout a console and stderr a file, and one
    /// answer for both put escape sequences in that file. Every refusal goes to stderr, so
    /// that was the log with the noise in it.
    ///
    /// Four ways to end up plain, and each is a real case: the listener service has no console
    /// and writes to its log file; a redirected stream is a file somebody reads; `NO_COLOR`
    /// is the convention every tool honours; and `--no-color` is the answer for a terminal
    /// that claims to understand escapes and does not.
    private static Palette PaletteFor(string[] args, bool asService, bool redirected)
    {
        if (asService
            || redirected
            || Environment.GetEnvironmentVariable("NO_COLOR") is not null
            || args.Contains("--no-color", StringComparer.Ordinal))
        {
            return Palette.None;
        }

        return VirtualTerminal.TryEnable() ? Palette.Ansi : Palette.None;
    }

    private static async Task<int> Main(string[] args)
    {
        bool asService = WindowsServiceHelpers.IsWindowsService();

        // Which service this is, from the verb the service manager starts it with. A service
        // started with anything else is refused once it has answered the manager.
        RipcordService? role = asService ? RipcordService.ForVerb(args.FirstOrDefault()) : null;
        Palette palette = PaletteFor(args, asService, Console.IsOutputRedirected);
        Palette errorPalette = PaletteFor(args, asService, Console.IsErrorRedirected);

        // Taken out before anything parses arguments: every verb refuses an option it does not
        // know, and this one is answered before a verb is chosen.
        args = [.. args.Where(argument => argument != "--no-color")];

        // The configuration lives beside the binary: updating Ripcord is replacing the .exe.
        string binaryPath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "ripcord.exe");

        string defaultConfigPath = Path.Combine(AppContext.BaseDirectory, "ripcord.yaml");

        SystemClock clock = new();
        FileSnapshotStore snapshotStore = new();
        MachineCertificateStore certificates = new();

        // Starts in the logs folder and, for a command, is moved to wherever `ripcord.yaml`
        // asks once that file has been read. The first thing worth logging is often the reason
        // it cannot be, so the log cannot wait for it.
        DiagnosticOrigin origin = role?.Origin ?? DiagnosticOrigin.Command;

        FileDiagnosticLog diagnostics = new(
            clock,
            origin,
            DiagnosticDestination.Default(LogFolder.For(
                origin, Path.Combine(AppContext.BaseDirectory, LogFolder.Name))));

        // The publisher repeats its reads every fifteen seconds: a failure among them is
        // written once an hour, not every time. Its own journal lines are never held back.
        IDiagnosticLog portsLog = role == RipcordService.Publisher
            ? new RepeatLimitedDiagnosticLog(
                diagnostics, clock, PublishJournal.SummaryEvery, [PublishJournal.Operation])
            : diagnostics;

        RipcordCli cli = new(
            new RipcordPorts(
                new YamlConfigStore(),
                new WmiHypervProvider(Environment.MachineName, WmiTimeout, portsLog),
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
                    connection => Report(asService, diagnostics, connection)),
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
                new FileUpdateNoticeStore(
                    Path.Combine(AppContext.BaseDirectory, "update-notice.json")),
                new FileBinarySwap(),
                clock,
                portsLog,
                new FileDiagnosticLogReader()),
            new CliEnvironment(
                Environment.MachineName,
                defaultConfigPath,
                binaryPath,
                Console.In,
                Environment.UserName,
                null,
                ReleaseSigningKey,
                palette,
                errorPalette,
                asService,
                !asService && !Console.IsOutputRedirected));

        // Started by the service control manager rather than by a person. `sc start` waits for
        // a handshake — ServiceBase.Run — and a console loop never sends one, so the manager
        // waits its whole timeout and reports 1053: "the service did not respond in a timely
        // fashion". That is what the listener deployment has been hitting on every host.
        //
        // The same binary and the same verb either way; only who is asking changes.
        if (asService)
        {
            ServiceSetup setup = new(cli, args, diagnostics, defaultConfigPath, role);

            return (int)await RunAsServiceAsync(setup).ConfigureAwait(false);
        }

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

    /// The service control manager's half: answer the handshake, then run the verb it was
    /// installed with until the host is told to stop.
    ///
    /// Nothing that can fail runs here, before `RunAsync` has answered the manager. Opening the
    /// log used to, and the service account cannot write beside the binary: the process died
    /// before the handshake, `sc start` reported 1053, and nothing anywhere said why.
    ///
    /// `Stop-Service` cancels the token the listener is already built around, so shutting down
    /// is the path that was already there rather than a second one.
    private static async Task<ExitCode> RunAsServiceAsync(ServiceSetup setup)
    {
        // Qualified: this project's own namespace is Ripcord.Host.Windows, so a bare `Host`
        // binds to Ripcord.Host and the error names a namespace nobody wrote.
        HostApplicationBuilder builder =
            Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();

        builder.Services.AddWindowsService(options =>
            options.ServiceName = (setup.Role ?? RipcordService.Listener).Name);

        // Named rather than taken from the assembly: it is the source `service install`
        // registers, and an unregistered one cannot be written to by a virtual account.
        builder.Services.Configure<EventLogSettings>(settings =>
            settings.SourceName = DeploymentPlan.EventSource);

        ServiceOutcome outcome = new();

        builder.Services.AddHostedService(provider => new VerbService(
            setup,
            outcome,
            provider.GetRequiredService<IHostLifetime>(),
            provider.GetRequiredService<ILogger<VerbService>>(),
            provider.GetRequiredService<IHostApplicationLifetime>()));

        await builder.Build().RunAsync().ConfigureAwait(false);

        return outcome.Code;
    }

    /// `Role` is null for a verb no Ripcord service runs.
    private sealed record ServiceSetup(
        RipcordCli Cli,
        string[] Args,
        FileDiagnosticLog Diagnostics,
        string ConfigurationPath,
        RipcordService? Role);

    /// What the verb decided, carried back out of the hosted service.
    ///
    /// Returned from Main for anyone running the binary by hand. Windows does not read it: a
    /// service that stops itself reports `ServiceBase.ExitCode`, which is set alongside.
    private sealed class ServiceOutcome
    {
        public ExitCode Code { get; set; } = ExitCode.Success;
    }

    /// Runs one CLI verb for as long as the service is running.
    ///
    /// Runs after the handshake: the host starts hosted services only once the service manager
    /// has been answered. A verb that returns on its own — a configuration the listener is
    /// disabled in, a file that will not load — stops the service rather than leaving it
    /// reported as running with nothing behind it.
    private sealed class VerbService(
        ServiceSetup setup,
        ServiceOutcome outcome,
        IHostLifetime hostLifetime,
        ILogger<VerbService> logger,
        IHostApplicationLifetime lifetime) : BackgroundService
    {
        private static readonly Action<ILogger, string, Exception?> Stopped =
            LoggerMessage.Define<string>(
                LogLevel.Error, new EventId(1, "ServiceStopped"), "{Message}");

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (setup.Role is not { } role)
            {
                Stopped(
                    logger,
                    $"A Ripcord service was started with '{setup.Args.FirstOrDefault()}', which "
                        + "no Ripcord service runs. Run 'ripcord service install'.",
                    null);
                this.Stop(ExitCode.InvalidConfiguration, stoppingToken);
                return;
            }

            // The first line decides whether there is a log at all. Nothing else can say so
            // but the event log: a service has no console.
            if (setup.Diagnostics.TryWrite(ServiceStartup.Banner(
                    role, BuildInfo.VersionWithCommit, setup.ConfigurationPath)) is { } reason)
            {
                Stopped(
                    logger,
                    ServiceStartup.LogUnavailable(role, setup.Diagnostics.CurrentFile, reason),
                    null);
                this.Stop(ExitCode.LocalAccessFailure, stoppingToken);
                return;
            }

            this.RecordProcess(role);

            // What the verb would have printed on a console becomes lines of the same log.
            using DiagnosticTextWriter writer = new(setup.Diagnostics, role.Verb);

            try
            {
                ExitCode code = await setup.Cli
                    .RunAsync(setup.Args, writer, writer, stoppingToken)
                    .ConfigureAwait(false);

                this.Stop(code, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Not rethrown: the host would stop cleanly and report 0 anyway. The exit code
                // set here is what makes the stop abnormal to Windows. The verb has already
                // written the exception to the log.
                Stopped(logger, $"The Ripcord {role.Role} stopped on an error.", exception);
                this.Stop(ExitCode.LocalAccessFailure, stoppingToken);
            }
        }

        /// Read back by `ripcord service`: after an update, the build on disk is not the build
        /// running. Failing to write it stops nothing, and is logged.
        private void RecordProcess(RipcordService role)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetDirectoryName(setup.Diagnostics.CurrentFile)!, role.ProcessFile),
                    new ServiceProcess(BuildInfo.VersionWithCommit, Environment.ProcessId).Text());
            }
            // Outside the verb's try: anything escaping here would stop the listener.
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                _ = setup.Diagnostics.TryWrite(new DiagnosticEntry(
                    "service", $"the process record was not written: {exception.Message}", []));
            }
        }

        private void Stop(ExitCode code, CancellationToken stoppingToken)
        {
            // A stop somebody asked for is not a failure, whatever the verb made of the
            // cancellation.
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            outcome.Code = code;

            // The one exit code Windows reads for a service that stops itself.
            if (hostLifetime is ServiceBase service)
            {
                service.ExitCode = ServiceExitCode.ToWindows(code);
            }

            lifetime.StopApplication();
        }
    }
}
