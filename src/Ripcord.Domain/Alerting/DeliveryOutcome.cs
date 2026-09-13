namespace Ripcord.Domain.Alerting;

/// What actually left the host. Both lists are written for the console, one line each.
///
/// A partial delivery counts as delivered: the operator has been told, and re-sending to the
/// transport that worked because a second one failed is how a working mailbox fills up with
/// duplicates of an alert somebody already read.
public sealed record DeliveryOutcome(IReadOnlyList<string> Delivered, IReadOnlyList<string> Failed)
{
    public static DeliveryOutcome Nothing { get; } = new([], []);

    public bool AnyDelivered => this.Delivered.Count > 0;
}
