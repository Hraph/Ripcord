using Ripcord.Domain.Configuration;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Dashboard;
using Ripcord.Ports.Dashboard;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Deployment;
using Ripcord.Ports.Updates;
using Ripcord.Ports.Pairing;
using Ripcord.Ports;
using Ripcord.Ports.Diagnostics;
using Ripcord.Domain.Diagnostics;
using Ripcord.Ports.Configuration;
using Ripcord.Adapters.Fake;
using Ripcord.Tests.Alerting;
using Ripcord.Tests.Updates;
using Ripcord.Cli;
using Ripcord.Ports.Replication;

namespace Ripcord.Tests;

/// Nothing in Ripcord calls DateTime.Now, so every test has to supply one. Shared because
/// four copies of three lines is four places to drift.
public sealed class FixedClock(DateTimeOffset now, DateTimeOffset? localNow = null) : IClock
{
    public DateTimeOffset UtcNow => now;

    public DateTimeOffset LocalNow => localNow ?? now;
}

/// A clock a test moves forward, for what changes when the day does.
public sealed class MovableClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;

    public DateTimeOffset LocalNow => this.UtcNow;
}

/// For the commands that never listen or deploy but have to be handed something.
public sealed class NoOpPeerListener : IPeerListener
{
    public Task RunAsync(
        ListenerSettings settings, PeerRules rules, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class NoOpDeploymentExecutor : IDeploymentExecutor
{
    public ObservedDeployment Observe(DesiredDeployment desired, ObservedService service) => ObservedDeployment.Nothing;

    public ObservedService ObserveService() => ObservedService.Absent;

    public void Apply(DeploymentStep change, DesiredDeployment desired)
    {
    }
}

/// Asks for the page once, keeps it, and returns. The real server loops until cancelled; a
/// test wants the document, not the loop.
public sealed class CapturingDashboardServer : IDashboardServer
{
    public DashboardSettings? Served { get; private set; }

    public string? Page { get; private set; }

    public async Task RunAsync(
        DashboardSettings settings,
        Func<CancellationToken, Task<string>> page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);

        this.Served = settings;
        this.Page = await page(cancellationToken).ConfigureAwait(false);
    }
}

/// For the commands that never serve a page but have to be handed something.
public sealed class NoOpDashboardServer : IDashboardServer
{
    public Task RunAsync(
        DashboardSettings settings,
        Func<CancellationToken, Task<string>> page,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// For the commands that never update but have to be handed something.
public sealed class NoReleaseSource : IReleaseSource
{
    public Task<FetchedRelease> FetchAsync(string version, CancellationToken cancellationToken) =>
        Task.FromResult(FetchedRelease.Failed("no release source is wired"));
}

public sealed class NoBinarySwap : IBinarySwap
{
    public StagedBinaries Observe(string binaryPath) => new(false, false);

    public void Apply(UpdateStep move, StagedRelease release, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("this test was not expecting a binary to move");

    public void Restore(string binaryPath)
    {
    }

        public string? Swapped { get; private set; }

        

        public SwapOutcome SwapWithPrevious(string binaryPath)
        {
            this.Swapped = binaryPath;
            return SwapOutcome.Exchanged;
        }
}

/// Remembers what it was told, which is all the real one does across two processes.
public sealed class MemoryUpdateNoticeStore(UpdateNotice? known = null) : IUpdateNoticeStore
{
    public UpdateNotice? Notice { get; private set; } = known;

    public UpdateNotice? Read() => this.Notice;

    public void Write(UpdateNotice notice) => this.Notice = notice;
}

/// A diagnostic log that keeps its lines in memory. Used where a test asserts what was
/// recorded; `SilentDiagnosticLog` is the one for tests that only need the port filled.
public sealed class RecordingDiagnosticLog : IDiagnosticLog
{
    private readonly List<DiagnosticEntry> written = [];

    public IReadOnlyList<DiagnosticEntry> Written => this.written;

    public DiagnosticDestination? Destination { get; private set; }

    public void Write(DiagnosticEntry entry) => this.written.Add(entry);

    public void SendTo(DiagnosticDestination destination) => this.Destination = destination;
}

/// A logs folder with nothing in it.
public sealed class NoLogs : IDiagnosticLogReader
{
    public LogReading? Tail(string path, int maxLines) => null;
}

public sealed class SilentDiagnosticLog : IDiagnosticLog
{
    public void Write(DiagnosticEntry entry)
    {
    }

    public void SendTo(DiagnosticDestination destination)
    {
    }
}

/// A configuration store that only reads. Most tests never write one, and every one of them
/// would otherwise carry two members saying so.
public abstract class ReadOnlyConfigStore : IConfigStore
{
    public abstract ConfigurationRead Read(string path);

    public virtual string? ReadText(string path) => null;

    public virtual ConfigurationWrite Write(string path, string content, string keepAs) =>
        ConfigurationWrite.Failed("this test's configuration store does not write");
}

/// The store `ripcord init` is tested against: it remembers what was written and what the
/// previous file was kept as.
public sealed class MemoryConfigStore(ConfigurationRead read, string? text = null)
    : ReadOnlyConfigStore
{
    public string? Written { get; private set; }

    public string? Kept { get; private set; }

    public override ConfigurationRead Read(string path) => read;

    public override string? ReadText(string path) => text;

    public override ConfigurationWrite Write(string path, string content, string keepAs)
    {
        this.Written = content;
        this.Kept = text is null ? null : keepAs;

        return ConfigurationWrite.Succeeded(this.Kept);
    }
}

/// `YamlConfigStore` reads a path, and half these tests have text. One temp file, written and
/// deleted, rather than the same six lines in four places.
public static class Yaml
{
    public static ConfigurationRead Read(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".yaml");

        try
        {
            File.WriteAllText(path, text);
            return new Ripcord.Adapters.Yaml.YamlConfigStore().Read(path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// A host nothing has been deployed to. `TestPorts` needs an executor and most verbs never
/// reach it; the ones that do bring their own.
public sealed class UntouchedHost : IDeploymentExecutor
{
    public ObservedDeployment Observe(DesiredDeployment desired, ObservedService service) =>
        new(false, null, false, null, null, false, false);

    public ObservedService ObserveService() => ObservedService.Absent;

    public void Apply(DeploymentStep change, DesiredDeployment desired)
    {
    }
}

/// Every port filled with a harmless fake, so a test about one verb does not have to name
/// seventeen of them. Each milestone adds another port, and this is the one place that has to
/// know.
public static class TestPorts
{
    public static readonly DateTimeOffset Now =
        new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    public static RipcordPorts With(
        IConfigStore configStore, IHypervProvider? provider = null) =>
        new(
            configStore,
            provider ?? new FakeHypervProvider(FakeScenarios.Healthy(Now)),
            FakeHostSystemProvider.Target(),
            FakeCertificateProvider.Valid(
                Tests.Configuration.ValidDocument.LocalThumbprint, "CN=HV-REPLICA-01"),
            FakePeerChannel.Absent(),
            new InMemorySnapshotStore(),
            new UntouchedHost(),
            new NoOpPeerListener(),
            new NoOpDashboardServer(),
            new InMemoryAuditLog(),
            new StubNotifier(),
            new MemoryAlertStateStore(),
            StubReleaseFeed.Unreachable(),
            new NoReleaseSource(),
            new MemoryUpdateNoticeStore(),
            new NoBinarySwap(),
            new FixedClock(Now),
            new SilentDiagnosticLog(),
            new NoLogs());
}
