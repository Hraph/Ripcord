using Ripcord.Application.Alerting;
using Ripcord.Domain.Alerting;
using Ripcord.Domain.Checks;

namespace Ripcord.Tests.Alerting;

/// The use case around the policy: what is written down, what is not, and what a run that
/// could not reach its relay leaves behind for the next one.
public class AlertDispatchTests
{
    private static readonly DateTimeOffset Noon =
        new(2026, 9, 13, 12, 0, 0, TimeSpan.FromHours(2));

    private static readonly AlertingSettings Settings =
        new(true, TimeSpan.FromHours(24), null, null, null);

    [Fact]
    public async Task A_delivered_notification_is_written_down_so_the_next_run_stays_quiet()
    {
        StubNotifier notifier = new();
        MemoryAlertStateStore store = new();

        AlertOutcome outcome = await Dispatch(store, notifier, Reports.WithSwitchMismatch());

        Assert.Equal(AlertAction.Send, outcome.Decision.Action);
        Assert.Equal(AlertKind.Raised, Assert.Single(notifier.Sent).Kind);
        Assert.Equal(Noon, store.State.NotifiedAt);
        Assert.Contains("notified", string.Join(" ", outcome.Notes), StringComparison.Ordinal);
    }

    /// Nothing left the host, so nothing is recorded as having left it. A run that writes
    /// "notified" after the relay refused is a pair that stays broken in silence for a day.
    [Fact]
    public async Task A_notification_that_reached_nobody_is_not_written_down()
    {
        StubNotifier notifier = new() { Outcome = new DeliveryOutcome([], ["smtp.example.net: connection refused"]) };
        MemoryAlertStateStore store = new();

        AlertOutcome outcome = await Dispatch(store, notifier, Reports.WithSwitchMismatch());

        Assert.Equal(0, store.Writes);
        Assert.Contains(
            outcome.Notes, note => note.Contains("connection refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_notification_that_reached_one_of_two_transports_is_written_down()
    {
        StubNotifier notifier = new()
        {
            Outcome = new DeliveryOutcome(["ops@example.net"], ["hooks.example.net: 503"]),
        };
        MemoryAlertStateStore store = new();

        AlertOutcome outcome = await Dispatch(store, notifier, Reports.WithSwitchMismatch());

        Assert.Equal(Noon, store.State.NotifiedAt);
        Assert.Contains(outcome.Notes, note => note.Contains("503", StringComparison.Ordinal));
    }

    /// `--dry-run` is what gets read before the scheduled task is switched on, so it has to
    /// say where the mail would go — and leave the state exactly as it found it.
    [Fact]
    public async Task A_dry_run_sends_nothing_and_records_nothing()
    {
        StubNotifier notifier = new() { Description = ["mail to ops@example.net via smtp.example.net:587"] };
        MemoryAlertStateStore store = new();

        AlertOutcome outcome = await Dispatch(
            store, notifier, Reports.WithSwitchMismatch(), dryRun: true);

        Assert.Empty(notifier.Sent);
        Assert.Equal(0, store.Writes);
        Assert.NotNull(outcome.Sent);
        Assert.Contains(
            outcome.Notes, note => note.Contains("would notify", StringComparison.Ordinal));
        Assert.Contains(
            outcome.Notes, note => note.Contains("ops@example.net", StringComparison.Ordinal));
    }

    /// A file rewritten every fifteen minutes with the same bytes is a file that looks
    /// touched when nothing happened.
    [Fact]
    public async Task A_run_that_changes_nothing_leaves_the_state_file_alone()
    {
        MemoryAlertStateStore store = new();
        StubNotifier notifier = new();

        await Dispatch(store, notifier, Reports.WithSwitchMismatch());
        int afterFirst = store.Writes;

        await Dispatch(store, notifier, Reports.WithSwitchMismatch());

        Assert.Equal(afterFirst, store.Writes);
    }

    [Fact]
    public async Task A_held_notification_is_written_down_so_it_survives_the_night()
    {
        MemoryAlertStateStore store = new();
        StubNotifier notifier = new();
        Assert.True(QuietHours.TryParse("00:00-07:00", out QuietHours? window));

        AlertOutcome outcome = await Dispatch(
            store, notifier, Reports.WithSwitchMismatch(),
            settings: Settings with { QuietHours = window },
            now: new DateTimeOffset(2026, 9, 13, 2, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal(AlertAction.Hold, outcome.Decision.Action);
        Assert.Empty(notifier.Sent);
        Assert.NotNull(store.State.HeldSince);
        Assert.Contains(outcome.Notes, note => note.Contains("quiet hours", StringComparison.Ordinal));
    }

    /// Off means the file is never even opened: a host that notifies nobody must behave
    /// exactly as it did before this milestone existed. It is still said out loud, because
    /// somebody asked for a notification and is entitled to know none is coming.
    [Fact]
    public async Task Alerting_switched_off_touches_nothing_but_says_so()
    {
        MemoryAlertStateStore store = new();
        StubNotifier notifier = new();

        AlertOutcome outcome = await Dispatch(
            store, notifier, Reports.WithSwitchMismatch(), settings: AlertingSettings.Disabled());

        Assert.Equal(AlertAction.Nothing, outcome.Decision.Action);
        Assert.Equal(0, store.Reads);
        Assert.Equal(0, store.Writes);
        Assert.Empty(notifier.Sent);

        Assert.Contains(
            "switched off", Assert.Single(outcome.Notes), StringComparison.Ordinal);
    }

    /// The finding is on the console either way. A state file that cannot be written is worth
    /// saying out loud and is not worth failing the check for.
    [Fact]
    public async Task A_state_file_that_cannot_be_written_is_reported_and_not_fatal()
    {
        MemoryAlertStateStore store = new() { WriteFails = true };
        StubNotifier notifier = new();

        AlertOutcome outcome = await Dispatch(store, notifier, Reports.WithSwitchMismatch());

        Assert.Single(notifier.Sent);
        Assert.Contains(
            outcome.Notes,
            note => note.Contains("cannot record", StringComparison.Ordinal));
    }

    private static Task<AlertOutcome> Dispatch(
        MemoryAlertStateStore store,
        StubNotifier notifier,
        CheckReport report,
        bool dryRun = false,
        AlertingSettings? settings = null,
        DateTimeOffset? now = null) =>
        new AlertDispatch(store, notifier, new FixedClock(Noon, now ?? Noon))
            .ExecuteAsync(report, settings ?? Settings, dryRun, CancellationToken.None);
}
