using Ripcord.Adapters.Fake;
using Ripcord.Application;
using Ripcord.Domain;
using Ripcord.Domain.Diagnostics;
using Ripcord.Domain.Pairing;
using Ripcord.Ports.Pairing;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Application;

/// The publishing service's loop: it publishes until stopped, and its log opens and closes.
public class SnapshotPublishingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    /// Stopped by the store after three writes rather than by a timer, so a loaded machine
    /// cannot cancel it in the middle of a read.
    [Fact]
    public async Task It_publishes_until_stopped_and_says_both()
    {
        using CancellationTokenSource stop = new();
        StoppingStore store = new(stop, after: 3);
        RecordingDiagnosticLog log = new();

        await Publishing(store, log).RunAsync(Configurations.Create(), stop.Token);

        Assert.Equal(3, store.Writes);
        Assert.StartsWith("publishing every 15 s to", log.Written[0].Message, StringComparison.Ordinal);
        Assert.StartsWith("stopping:", log.Written[^1].Message, StringComparison.Ordinal);
    }

    /// The review's finding: an exception nobody foresaw used to end the service for good.
    [Fact]
    public async Task An_unforeseen_failure_is_said_and_the_loop_goes_on()
    {
        using CancellationTokenSource stop = new();
        StoppingStore store = new(stop, after: 2) { FailFirst = new InvalidOperationException("boom") };
        RecordingDiagnosticLog log = new();

        await Publishing(store, log).RunAsync(Configurations.Create(), stop.Token);

        Assert.Equal(2, store.Writes);
        Assert.Contains(log.Written, entry => entry.Message == "publishing failed unexpectedly");
        Assert.Contains(log.Written, entry => entry.Message.Contains("boom", StringComparison.Ordinal));
        Assert.StartsWith("stopping:", log.Written[^1].Message, StringComparison.Ordinal);
    }

    /// A cancellation the service did not ask for is a failure like any other, not a stop.
    [Fact]
    public async Task A_cancellation_from_inside_a_publication_does_not_end_the_loop()
    {
        using CancellationTokenSource stop = new();
        StoppingStore store = new(stop, after: 2) { FailFirst = new OperationCanceledException("timed out") };
        RecordingDiagnosticLog log = new();

        await Publishing(store, log).RunAsync(Configurations.Create(), stop.Token);

        Assert.Equal(2, store.Writes);
        Assert.Contains(log.Written, entry => entry.Message == "publishing failed unexpectedly");
    }

    /// Counts successful writes and cancels the loop after the last one it was asked for.
    private sealed class StoppingStore(CancellationTokenSource stop, int after) : ISnapshotStore
    {
        public int Writes { get; private set; }

        public Exception? FailFirst { get; set; }

        public void Write(string path, HostSnapshot snapshot)
        {
            if (this.FailFirst is { } failure)
            {
                this.FailFirst = null;
                throw failure;
            }

            if (++this.Writes == after)
            {
                stop.Cancel();
            }
        }

        public HostSnapshot? Read(string path) => null;
    }

    [Fact]
    public async Task A_disabled_listener_ends_the_loop_at_once_and_says_why()
    {
        RecordingDiagnosticLog log = new();

        await Publishing(new InMemorySnapshotStore(), log).RunAsync(
            Configurations.Create(document => document.Listener!.Enabled = false), CancellationToken.None);

        Assert.Equal("the listener is disabled: nothing to publish", Assert.Single(log.Written).Message);
    }

    private static SnapshotPublishing Publishing(ISnapshotStore store, RecordingDiagnosticLog log) =>
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
