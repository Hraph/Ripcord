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

namespace Ripcord.Tests;

/// Nothing in Ripcord calls DateTime.Now, so every test has to supply one. Shared because
/// four copies of three lines is four places to drift.
public sealed class FixedClock(DateTimeOffset now, DateTimeOffset? localNow = null) : IClock
{
    public DateTimeOffset UtcNow => now;

    public DateTimeOffset LocalNow => localNow ?? now;
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
    public ObservedDeployment Observe(DesiredDeployment desired) => ObservedDeployment.Nothing;

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

public sealed class SilentDiagnosticLog : IDiagnosticLog
{
    public void Write(DiagnosticEntry entry)
    {
    }

    public void SendTo(DiagnosticDestination destination)
    {
    }
}
