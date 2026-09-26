using Ripcord.Adapters.Fake;
using Ripcord.Application;
using Ripcord.Domain;
using Ripcord.Domain.Diagnostics;
using Ripcord.Domain.Pairing;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Application;

/// The publishing service's loop: it publishes until stopped, and its log opens and closes.
public class SnapshotPublishingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task It_publishes_until_stopped_and_says_both()
    {
        InMemorySnapshotStore store = new();
        RecordingDiagnosticLog log = new();
        using CancellationTokenSource stop = new(TimeSpan.FromMilliseconds(200));

        await Publishing(store, log).RunAsync(Configurations.Create(), stop.Token);

        Assert.NotEmpty(store.Written);
        Assert.StartsWith("publishing every 15 s to", log.Written[0].Message, StringComparison.Ordinal);
        Assert.StartsWith("stopping:", log.Written[^1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_disabled_listener_ends_the_loop_at_once_and_says_why()
    {
        RecordingDiagnosticLog log = new();

        await Publishing(new InMemorySnapshotStore(), log).RunAsync(
            Configurations.Create(document => document.Listener!.Enabled = false), CancellationToken.None);

        Assert.Equal("the listener is disabled: nothing to publish", Assert.Single(log.Written).Message);
    }

    private static SnapshotPublishing Publishing(InMemorySnapshotStore store, RecordingDiagnosticLog log) =>
        new(
            new PairReader(
                new LocalStateReader(
                    new FakeHypervProvider(FakeScenarios.Healthy(Now)),
                    FakeHostSystemProvider.Target(),
                    FakeCertificateProvider.Valid(ValidDocument.LocalThumbprint, "CN=HV-REPLICA-01"),
                    new SilentDiagnosticLog()),
                FakePeerChannel.Absent(),
                store,
                new FixedClock(Now),
                new BuildIdentity("0.8.0", "abc123"),
                new SilentDiagnosticLog()),
            new FixedClock(Now),
            log,
            TimeSpan.FromMilliseconds(5));
}
