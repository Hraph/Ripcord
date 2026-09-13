using Ripcord.Domain.Checks;

namespace Ripcord.Domain.Alerting;

/// What the last run left behind. Three facts and nothing else: which critical findings the
/// last notification covered, when it went out, and since when one is waiting for the quiet
/// window to end.
///
/// The fingerprint is the findings themselves rather than a hash, so the file a human opens
/// after a silent night says what the tool thought was wrong.
public sealed record AlertState(string Fingerprint, DateTimeOffset? NotifiedAt, DateTimeOffset? HeldSince)
{
    public static AlertState Clear { get; } = new("", null, null);

    internal IEnumerable<string> Keys =>
        this.Fingerprint.Length == 0 ? [] : this.Fingerprint.Split('\n');
}

public enum AlertAction
{
    /// Deliver it now.
    Send,

    /// Quiet hours. Kept, not dropped — it goes out when the window ends.
    Hold,

    /// Already notified, and nothing new since.
    Suppress,

    /// Nothing to say.
    Nothing,
}

public sealed record AlertRequest(
    CheckReport Report,
    AlertingSettings Settings,
    AlertState Previous,
    /// In the host's local time: the offset it carries is the one quiet hours are read in.
    DateTimeOffset Now);

/// What to do, what to send, and what the next run must be told. The reason is written for a
/// human because it is what `--dry-run` prints and what the console says after a run that
/// deliberately stayed silent.
public sealed record AlertDecision(
    AlertAction Action, Notification? Notification, AlertState State, string Reason);

/// Alerting is a delivery decision, not a second opinion on the pair: the trigger is the
/// critical findings `ripcord check` already produced (decision D7), never a rule of its own.
///
/// It notifies on transitions. A scheduled check runs every few minutes and the same broken
/// replication is in every one of those reports; sending all of them is how an operator
/// learns to filter the mailbox that was supposed to wake them.
public static class AlertPolicy
{
    public static AlertDecision Decide(AlertRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Settings.Enabled)
        {
            return new AlertDecision(
                AlertAction.Nothing, null, AlertState.Clear,
                "alerting is switched off on this node");
        }

        IReadOnlyList<Finding> criticals =
            [.. request.Report.Findings.Where(finding => finding.CountsAsCritical)];

        return criticals.Count == 0
            ? Cleared(request)
            : Raised(request, criticals);
    }

    /// Nothing was delivered, so there is nothing to say has recovered: a finding that
    /// appeared and cleared inside the quiet window is dropped, not delivered at dawn.
    private static AlertDecision Cleared(AlertRequest request)
    {
        if (request.Previous.NotifiedAt is null)
        {
            return new AlertDecision(
                AlertAction.Nothing, null, AlertState.Clear,
                request.Previous.HeldSince is null
                    ? "no critical finding, and none outstanding"
                    : "the held finding cleared before the quiet window ended");
        }

        if (IsQuiet(request))
        {
            return new AlertDecision(
                AlertAction.Hold,
                null,
                request.Previous with
                {
                    Fingerprint = "",
                    HeldSince = request.Previous.HeldSince ?? request.Now,
                },
                "the pair recovered during quiet hours; held until the window ends");
        }

        return new AlertDecision(
            AlertAction.Send,
            Notification.Recovered(request.Report),
            AlertState.Clear,
            "the last notified findings have all cleared");
    }

    private static AlertDecision Raised(AlertRequest request, IReadOnlyList<Finding> criticals)
    {
        string fingerprint = FingerprintOf(criticals);

        // A finding that was not in the last notification is news. A finding disappearing is
        // not: re-sending the survivors would restart the repeat threshold every time
        // something else recovered.
        bool anythingNew = fingerprint.Split('\n').Except(request.Previous.Keys).Any();

        bool repeatDue = request.Previous.NotifiedAt is { } notifiedAt
            && request.Now - notifiedAt >= request.Settings.RepeatAfter;

        bool held = request.Previous.HeldSince is not null;

        if (!held && !anythingNew && !repeatDue)
        {
            return new AlertDecision(
                AlertAction.Suppress, null, request.Previous,
                "already notified, and nothing new since");
        }

        DateTimeOffset raisedAt = request.Previous.HeldSince ?? request.Now;

        // The threshold is a delivery interval, not a licence to ignore the window: a repeat
        // coming due at 3 a.m. waits for the morning like anything else.
        if (IsQuiet(request))
        {
            return new AlertDecision(
                AlertAction.Hold,
                null,
                new AlertState(fingerprint, request.Previous.NotifiedAt, raisedAt),
                "quiet hours; held until the window ends");
        }

        return new AlertDecision(
            AlertAction.Send,
            Notification.Raised(request.Report, criticals, raisedAt),
            new AlertState(fingerprint, request.Now, null),
            held ? "the quiet window has ended"
                : anythingNew ? "a critical finding that was not notified before"
                : "still broken, and the repeat threshold has passed");
    }

    private static bool IsQuiet(AlertRequest request) =>
        request.Settings.QuietHours?.Covers(request.Now) == true;

    /// Rule and subject, ordered, so the same two findings in a different order are the same
    /// alert and a third one is a different one.
    private static string FingerprintOf(IReadOnlyList<Finding> criticals) =>
        string.Join(
            '\n',
            criticals
                .Select(finding => $"{finding.Rule.Id} {finding.Subject ?? "-"}")
                .Order(StringComparer.Ordinal));
}
