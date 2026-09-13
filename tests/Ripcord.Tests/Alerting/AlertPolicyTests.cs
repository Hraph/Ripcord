using Ripcord.Domain.Alerting;
using Ripcord.Domain.Checks;

namespace Ripcord.Tests.Alerting;

/// The whole of the alerting decision: what gets sent, what gets held, and what gets kept
/// quiet. A scheduled `ripcord check` runs this every few minutes, so the interesting cases
/// are all "the same report, again".
public class AlertPolicyTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 13, 12, 0, 0, TimeSpan.FromHours(2));

    private static readonly DateTimeOffset Midnight =
        new(2026, 9, 14, 0, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void A_pair_with_nothing_wrong_and_nothing_behind_it_sends_nothing()
    {
        AlertDecision decision = Decide(Reports.Clean(), AlertState.Clear, Noon);

        Assert.Equal(AlertAction.Nothing, decision.Action);
        Assert.Null(decision.Notification);
        Assert.Equal(AlertState.Clear, decision.State);
    }

    [Fact]
    public void The_first_critical_finding_is_sent()
    {
        AlertDecision decision = Decide(Reports.WithSwitchMismatch(), AlertState.Clear, Noon);

        Assert.Equal(AlertAction.Send, decision.Action);
        Assert.Equal(AlertKind.Raised, decision.Notification!.Kind);
        Assert.Equal(Noon, decision.State.NotifiedAt);
        Assert.Null(decision.State.HeldSince);
    }

    /// The exit criterion, in one test: a still-broken replication does not produce a second
    /// notification. An alert repeated every fifteen minutes is a filter rule waiting to be
    /// written.
    [Fact]
    public void The_same_finding_on_the_next_run_is_kept_quiet()
    {
        AlertState after = Decide(Reports.WithSwitchMismatch(), AlertState.Clear, Noon).State;

        AlertDecision again = Decide(
            Reports.WithSwitchMismatch(), after, Noon.AddMinutes(15));

        Assert.Equal(AlertAction.Suppress, again.Action);
        Assert.Null(again.Notification);
        Assert.Equal(after, again.State);
    }

    [Fact]
    public void The_same_finding_is_repeated_once_the_threshold_has_passed()
    {
        AlertState after = Decide(Reports.WithSwitchMismatch(), AlertState.Clear, Noon).State;

        AlertDecision again = Decide(
            Reports.WithSwitchMismatch(), after, Noon.AddHours(24));

        Assert.Equal(AlertAction.Send, again.Action);
        Assert.Equal(Noon.AddHours(24), again.State.NotifiedAt);
    }

    /// A second VM breaking is a new fact, and a new fact is a transition — it does not wait
    /// out the repeat threshold of the first one.
    [Fact]
    public void A_finding_that_was_not_in_the_last_notification_is_sent_at_once()
    {
        AlertState after = Decide(Reports.WithSwitchMismatch(), AlertState.Clear, Noon).State;

        AlertDecision widened = Decide(
            Reports.WithSwitchMismatchAndInvertedDirection(), after, Noon.AddMinutes(15));

        Assert.Equal(AlertAction.Send, widened.Action);
        Assert.Contains("2 critical findings", widened.Notification!.Subject);
    }

    /// One of two findings clearing is not news worth waking anybody for, and re-sending the
    /// remaining one would restart its repeat threshold every time something else recovers.
    [Fact]
    public void A_finding_disappearing_while_another_remains_is_kept_quiet()
    {
        AlertState after = Decide(
            Reports.WithSwitchMismatchAndInvertedDirection(), AlertState.Clear, Noon).State;

        AlertDecision narrowed = Decide(
            Reports.WithSwitchMismatch(), after, Noon.AddMinutes(15));

        Assert.Equal(AlertAction.Suppress, narrowed.Action);
    }

    [Fact]
    public void A_pair_that_recovers_is_notified_once_and_then_left_alone()
    {
        AlertState raised = Decide(Reports.WithSwitchMismatch(), AlertState.Clear, Noon).State;

        AlertDecision recovered = Decide(Reports.Clean(), raised, Noon.AddHours(1));

        Assert.Equal(AlertAction.Send, recovered.Action);
        Assert.Equal(AlertKind.Recovered, recovered.Notification!.Kind);
        Assert.Equal(AlertState.Clear, recovered.State);

        Assert.Equal(
            AlertAction.Nothing,
            Decide(Reports.Clean(), recovered.State, Noon.AddHours(2)).Action);
    }

    [Fact]
    public void A_finding_raised_during_quiet_hours_is_held_rather_than_dropped()
    {
        AlertDecision held = Decide(Reports.WithSwitchMismatch(), AlertState.Clear, Midnight);

        Assert.Equal(AlertAction.Hold, held.Action);
        Assert.Null(held.Notification);
        Assert.Equal(Midnight, held.State.HeldSince);
        Assert.Null(held.State.NotifiedAt);
    }

    [Fact]
    public void A_held_finding_is_delivered_when_the_window_ends()
    {
        AlertState held = Decide(Reports.WithSwitchMismatch(), AlertState.Clear, Midnight).State;

        AlertDecision delivered = Decide(
            Reports.WithSwitchMismatch(), held, Midnight.AddHours(7));

        Assert.Equal(AlertAction.Send, delivered.Action);
        Assert.Equal(Midnight.AddHours(7), delivered.State.NotifiedAt);
        Assert.Null(delivered.State.HeldSince);
        Assert.Contains("raised at", delivered.Notification!.Body, StringComparison.Ordinal);
    }

    /// It stays held rather than being re-held from scratch: the delivered notification has
    /// to say when the finding actually appeared, not when the last run noticed it again.
    [Fact]
    public void A_finding_held_overnight_keeps_the_instant_it_was_raised()
    {
        AlertState held = Decide(Reports.WithSwitchMismatch(), AlertState.Clear, Midnight).State;

        AlertDecision still = Decide(
            Reports.WithSwitchMismatch(), held, Midnight.AddHours(3));

        Assert.Equal(AlertAction.Hold, still.Action);
        Assert.Equal(Midnight, still.State.HeldSince);
    }

    /// Nothing was ever sent, so there is nothing to say has recovered. Waking someone at
    /// 07:00 to tell them about a problem that fixed itself at 02:00 is how alerting loses
    /// its audience.
    [Fact]
    public void A_finding_that_clears_before_the_quiet_window_ends_is_dropped()
    {
        AlertState held = Decide(Reports.WithSwitchMismatch(), AlertState.Clear, Midnight).State;

        AlertDecision cleared = Decide(Reports.Clean(), held, Midnight.AddHours(2));

        Assert.Equal(AlertAction.Nothing, cleared.Action);
        Assert.Equal(AlertState.Clear, cleared.State);
    }

    [Fact]
    public void A_finding_appearing_while_another_is_held_widens_what_will_be_sent()
    {
        AlertState held = Decide(Reports.WithSwitchMismatch(), AlertState.Clear, Midnight).State;

        AlertDecision widened = Decide(
            Reports.WithSwitchMismatchAndInvertedDirection(), held, Midnight.AddHours(1));

        Assert.Equal(AlertAction.Hold, widened.Action);
        Assert.Equal(Midnight, widened.State.HeldSince);

        AlertDecision delivered = Decide(
            Reports.WithSwitchMismatchAndInvertedDirection(), widened.State, Midnight.AddHours(7));

        Assert.Contains("2 critical findings", delivered.Notification!.Subject);
    }

    /// A recovery waiting out the window also carries a held instant. A fresh finding after
    /// it is dated from now, not from the moment the last one cleared.
    [Fact]
    public void A_finding_appearing_after_a_held_recovery_is_dated_from_now()
    {
        AlertState notified = Decide(Reports.WithSwitchMismatch(), AlertState.Clear, Noon).State;
        AlertState heldRecovery = Decide(Reports.Clean(), notified, Midnight).State;

        AlertDecision delivered = Decide(
            Reports.WithSwitchMismatchAndInvertedDirection(),
            Decide(Reports.WithSwitchMismatchAndInvertedDirection(), heldRecovery, Midnight.AddHours(3)).State,
            Midnight.AddHours(7));

        Assert.Equal(AlertAction.Send, delivered.Action);
        Assert.Contains(
            $"raised at {Midnight.AddHours(3):yyyy-MM-dd HH:mm}",
            delivered.Notification!.Body,
            StringComparison.Ordinal);
    }

    /// The threshold is a delivery interval, not a decision to skip the window: a repeat that
    /// comes due at 3 a.m. waits for the morning like any other.
    [Fact]
    public void A_repeat_falling_inside_the_quiet_window_is_held()
    {
        AlertState after = Decide(Reports.WithSwitchMismatch(), AlertState.Clear, Noon).State;

        AlertDecision repeat = Decide(
            Reports.WithSwitchMismatch(), after, Noon.AddHours(35));

        Assert.Equal(AlertAction.Hold, repeat.Action);
    }

    /// Warnings never notify. Only code 1 does, which is the same trigger the scheduled task
    /// already has — there is no second detection path.
    [Fact]
    public void A_warning_on_its_own_notifies_nobody()
    {
        AlertDecision decision = Decide(Reports.WithWarning(), AlertState.Clear, Noon);

        Assert.Equal(AlertAction.Nothing, decision.Action);
    }

    /// An acknowledged critical does not set the exit code, so it must not send a mail
    /// either: two answers to "is this pair broken" would be one answer too many.
    [Fact]
    public void An_acknowledged_critical_notifies_nobody()
    {
        AlertDecision decision = Decide(Reports.WithAcknowledgedMismatch(Noon), AlertState.Clear, Noon);

        Assert.Equal(AlertAction.Nothing, decision.Action);
    }

    /// Off is off: a host with no mail relay must not accumulate held alerts that arrive the
    /// day somebody switches alerting on.
    [Fact]
    public void Alerting_switched_off_decides_nothing_at_all()
    {
        AlertDecision decision = AlertPolicy.Decide(new AlertRequest(
            Reports.WithSwitchMismatch(), AlertingSettings.Disabled(), AlertState.Clear, Noon));

        Assert.Equal(AlertAction.Nothing, decision.Action);
        Assert.Equal(AlertState.Clear, decision.State);
    }

    private static readonly AlertingSettings Settings = new(
        true, TimeSpan.FromHours(24), Quiet(), null, null);

    private static QuietHours Quiet()
    {
        Assert.True(QuietHours.TryParse("22:00-07:00", out QuietHours? window));
        return window!;
    }

    private static AlertDecision Decide(
        CheckReport report, AlertState previous, DateTimeOffset now) =>
        AlertPolicy.Decide(new AlertRequest(report, Settings, previous, now));
}
