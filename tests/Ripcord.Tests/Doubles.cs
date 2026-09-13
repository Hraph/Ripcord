using Ripcord.Domain.Configuration;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Dashboard;
using Ripcord.Ports.Dashboard;
using Ripcord.Ports.Deployment;
using Ripcord.Ports.Pairing;
using Ripcord.Ports;

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
