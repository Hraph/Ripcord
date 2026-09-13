using Ripcord.Domain.Alerting;
using Ripcord.Ports.Alerting;

namespace Ripcord.Tests.Alerting;

/// A transport that records rather than sends, and refuses on demand.
internal sealed class StubNotifier : INotifier
{
    public List<Notification> Sent { get; } = [];

    public DeliveryOutcome Outcome { get; init; } = new(["ops@example.net"], []);

    public IReadOnlyList<string> Description { get; init; } = ["mail to ops@example.net"];

    public IReadOnlyList<string> Describe(AlertingSettings settings) => this.Description;

    public Task<DeliveryOutcome> SendAsync(
        AlertingSettings settings, Notification notification, CancellationToken cancellationToken)
    {
        this.Sent.Add(notification);
        return Task.FromResult(this.Outcome);
    }
}

/// The state file, in memory, counting the times it was touched: "did this run rewrite the
/// file" is half of what the dispatch tests are about.
internal sealed class MemoryAlertStateStore : IAlertStateStore
{
    public AlertState State { get; private set; } = AlertState.Clear;

    public int Reads { get; private set; }

    public int Writes { get; private set; }

    public bool WriteFails { get; init; }

    public AlertState Read()
    {
        this.Reads++;
        return this.State;
    }

    public void Write(AlertState state)
    {
        if (this.WriteFails)
        {
            throw new IOException("the state file is read-only");
        }

        this.Writes++;
        this.State = state;
    }
}
