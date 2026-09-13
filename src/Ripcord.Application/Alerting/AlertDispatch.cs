using Ripcord.Domain.Alerting;
using Ripcord.Domain.Checks;
using Ripcord.Ports.Alerting;
using Ripcord.Ports;

namespace Ripcord.Application.Alerting;

/// What the run decided, what it sent, and what it wants said out loud. The notes go to
/// stderr beside the check report: a notification that could not leave the host is a
/// degraded state, and degradations are never silent.
public sealed record AlertOutcome(
    AlertDecision Decision, Notification? Sent, IReadOnlyList<string> Notes)
{
    public static AlertOutcome Silent(AlertDecision decision) => new(decision, null, []);
}

/// Runs the alerting decision and carries out whatever it decided. Nothing here judges the
/// pair: the report arrives already judged, and what to do with it is AlertPolicy's.
///
/// **Delivery comes before the record of it.** A state file written first would turn a relay
/// that was down for a minute into a pair that stays broken in silence until the repeat
/// threshold comes round.
public sealed class AlertDispatch(IAlertStateStore store, INotifier notifier, IClock clock)
{
    public async Task<AlertOutcome> ExecuteAsync(
        CheckReport report,
        AlertingSettings settings,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Enabled)
        {
            return AlertOutcome.Silent(AlertPolicy.Decide(
                new AlertRequest(report, settings, AlertState.Clear, clock.LocalNow)));
        }

        AlertState previous = store.Read();

        AlertDecision decision = AlertPolicy.Decide(
            new AlertRequest(report, settings, previous, clock.LocalNow));

        if (decision.Notification is not { } notification)
        {
            List<string> notes = [];

            if (decision.Action == AlertAction.Hold)
            {
                notes.Add($"{decision.Reason}.");
            }

            Record(decision.State, previous, dryRun, notes);
            return new AlertOutcome(decision, null, notes);
        }

        return dryRun
            ? new AlertOutcome(decision, notification, [.. WouldNotify(settings, notification)])
            : await this.DeliverAsync(decision, notification, settings, previous, cancellationToken)
                .ConfigureAwait(false);
    }

    private async Task<AlertOutcome> DeliverAsync(
        AlertDecision decision,
        Notification notification,
        AlertingSettings settings,
        AlertState previous,
        CancellationToken cancellationToken)
    {
        DeliveryOutcome delivery = await notifier
            .SendAsync(settings, notification, cancellationToken)
            .ConfigureAwait(false);

        List<string> notes =
        [
            .. delivery.Delivered.Select(target => $"notified {target}."),
            .. delivery.Failed.Select(failure => $"could not notify {failure}."),
        ];

        if (delivery.AnyDelivered)
        {
            Record(decision.State, previous, dryRun: false, notes);
        }

        return new AlertOutcome(decision, delivery.AnyDelivered ? notification : null, notes);
    }

    private IEnumerable<string> WouldNotify(AlertingSettings settings, Notification notification) =>
    [
        $"would notify: {notification.Subject}",
        .. notifier.Describe(settings).Select(target => $"would notify {target}."),
    ];

    /// Only when it changed: a file rewritten every fifteen minutes with the same bytes looks
    /// touched when nothing happened, and the timestamp is what a human reads first.
    private void Record(
        AlertState state, AlertState previous, bool dryRun, List<string> notes)
    {
        if (dryRun || state == previous)
        {
            return;
        }

        try
        {
            store.Write(state);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The finding is on the console either way. Failing the check over the file that
            // remembers what was already said would be the tail wagging the dog.
            notes.Add($"cannot record what was notified: {exception.Message}.");
        }
    }
}
