using Ripcord.Domain.Alerting;

namespace Ripcord.Ports.Alerting;

/// Carries one notification to whatever the configuration names — a relay, a webhook, or
/// both. It decides nothing about whether to send: that was settled by AlertPolicy before
/// this port is ever reached.
///
/// It never throws for a transport that refused. A relay that is down is a degraded state to
/// report, not a reason for `ripcord check` to fail: the finding it was carrying is the thing
/// that matters, and it is already on the console.
public interface INotifier
{
    /// What a send would do, for `--dry-run`. One line per transport.
    IReadOnlyList<string> Describe(AlertingSettings settings);

    Task<DeliveryOutcome> SendAsync(
        AlertingSettings settings, Notification notification, CancellationToken cancellationToken);
}
