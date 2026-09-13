using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;
using Ripcord.Domain.TestFailover;
using Ripcord.Ports.Replication;

namespace Ripcord.Tests;

/// A base for doubles that stand in for Hyper-V in a read-only command. Every mutating
/// operation refuses rather than quietly doing nothing: a use case that started calling one
/// would fail loudly here instead of passing a test that no longer exercises what it claims.
public abstract class ReadOnlyHypervProvider : IHypervProvider
{
    public abstract Task<HostState> GetLocalStateAsync(CancellationToken cancellationToken);

    public Task<IReadOnlyList<HostSwitch>> GetSwitchesAsync(CancellationToken cancellationToken) =>
        Refuse<IReadOnlyList<HostSwitch>>();

    public Task<IReadOnlyList<TestVm>> GetTestVmsAsync(CancellationToken cancellationToken) =>
        Refuse<IReadOnlyList<TestVm>>();

    public Task AttachTestNetworkAsync(
        string vmName, string? switchName, CancellationToken cancellationToken) =>
        Refuse<object>();

    public Task<TestVm> StartTestFailoverAsync(
        string vmName, CancellationToken cancellationToken) =>
        Refuse<TestVm>();

    public Task StopTestFailoverAsync(string vmName, CancellationToken cancellationToken) =>
        Refuse<object>();

    public Task StartTestVmAsync(string testVmName, CancellationToken cancellationToken) =>
        Refuse<object>();

    public Task<Heartbeat> ReadHeartbeatAsync(
        string testVmName, CancellationToken cancellationToken) =>
        Refuse<Heartbeat>();

    public Task ShutDownVmAsync(string vmName, CancellationToken cancellationToken) =>
        Refuse<object>();

    public Task PrepareFailoverAsync(string vmName, CancellationToken cancellationToken) =>
        Refuse<object>();

    public Task StartFailoverAsync(string vmName, CancellationToken cancellationToken) =>
        Refuse<object>();

    public Task ReverseReplicationAsync(string vmName, CancellationToken cancellationToken) =>
        Refuse<object>();

    public Task StartVmAsync(string vmName, CancellationToken cancellationToken) =>
        Refuse<object>();

    public Task CancelFailoverAsync(string vmName, CancellationToken cancellationToken) =>
        Refuse<object>();

    public Task SetAutomaticStartActionAsync(
        string vmName, AutomaticStartAction action, CancellationToken cancellationToken) =>
        Refuse<object>();

    private static Task<T> Refuse<T>() =>
        throw new NotSupportedException("this double stands in for a read-only command");
}
