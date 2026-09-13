using Ripcord.Adapters.Notify;
using Ripcord.Domain.Alerting;
using Ripcord.Ports.Alerting;

namespace Ripcord.Tests.Adapters;

/// The transport itself, without a relay. Nothing here reaches the network: these are the
/// refusals that happen before a socket is opened, and every one of them has to read as a
/// delivery that failed rather than as an exception out of `ripcord check`.
public class TransportNotifierTests
{
    private static readonly Notification Notification =
        new(AlertKind.Raised, "ripcord: 1 critical finding", "the body");

    /// MailMessage refuses an address it will not put in a header. That refusal must not take
    /// down the check whose finding the mail was carrying.
    [Fact]
    public async Task An_address_the_framework_refuses_is_a_failed_delivery_not_a_crash()
    {
        DeliveryOutcome outcome = await Notifier().SendAsync(
            Settings(new SmtpSettings(
                "smtp.example.net", 587, true, "not an address", ["ops@example.net"], null, null)),
            Notification,
            CancellationToken.None);

        Assert.Empty(outcome.Delivered);
        Assert.Single(outcome.Failed);
    }

    /// A named secret this host does not hold is a refusal, not an anonymous attempt: the
    /// relay would otherwise reject it with a message nobody can trace back to a missing
    /// environment variable.
    [Fact]
    public async Task A_secret_the_host_does_not_hold_is_a_failed_delivery()
    {
        DeliveryOutcome outcome = await Notifier().SendAsync(
            Settings(new SmtpSettings(
                "smtp.example.net", 587, true, "ripcord@example.net", ["ops@example.net"],
                "ripcord@example.net", "RIPCORD_SECRET_THAT_IS_NOT_SET")),
            Notification,
            CancellationToken.None);

        Assert.Empty(outcome.Delivered);
        Assert.Contains(
            "RIPCORD_SECRET_THAT_IS_NOT_SET",
            string.Join(" ", outcome.Failed),
            StringComparison.Ordinal);
    }

    /// `--dry-run` prints this before the scheduled task is ever switched on, so it has to
    /// name the relay and the recipients rather than say "smtp".
    [Fact]
    public void The_description_names_where_a_notification_would_go()
    {
        IReadOnlyList<string> described = Notifier().Describe(Settings(new SmtpSettings(
            "smtp.example.net", 587, true, "ripcord@example.net", ["ops@example.net"],
            null, null)) with
        {
            Webhook = new WebhookSettings(new Uri("https://hooks.example.net/ripcord")),
        });

        Assert.Equal(
            ["ops@example.net via smtp.example.net:587", "hooks.example.net/ripcord"], described);
    }

    private static AlertingSettings Settings(SmtpSettings smtp) =>
        new(true, TimeSpan.FromHours(24), null, smtp, null);

    private static TransportNotifier Notifier() =>
        new(new NoSecrets(), TimeSpan.FromSeconds(1));

    /// A host that holds no secret at all, which is every host until somebody sets one.
    private sealed class NoSecrets : ISecretStore
    {
        public string? Find(string name) => null;
    }
}
