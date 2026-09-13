using Ripcord.Domain.Configuration;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Pairing;
using Ripcord.Ports.Deployment;
using Ripcord.Ports.Pairing;
using Ripcord.Ports;

namespace Ripcord.Tests;

/// Nothing in Ripcord calls DateTime.Now, so every test has to supply one. Shared because
/// four copies of three lines is four places to drift.
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow => now;
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
