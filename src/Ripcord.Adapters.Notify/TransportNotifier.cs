using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Ripcord.Domain.Alerting;
using Ripcord.Ports.Alerting;

namespace Ripcord.Adapters.Notify;

/// Carries a notification to the transports the configuration names. It translates and it
/// reports; whether to send at all was settled by AlertPolicy, and which transports exist is
/// read straight off the settings rather than decided here.
///
/// Nothing throws out of this class. A relay that is down is a line on the console, not an
/// exception that takes down the `ripcord check` whose finding was the point.
public sealed class TransportNotifier(ISecretStore secrets, TimeSpan timeout) : INotifier
{
    /// Long enough for a relay that is merely slow, short enough that a scheduled check does
    /// not sit on a black-holed port until the next run overlaps it.
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    public IReadOnlyList<string> Describe(AlertingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        List<string> lines = [];

        if (settings.Smtp is { } smtp)
        {
            lines.Add(
                $"{string.Join(", ", smtp.To)} via {smtp.Host}:{smtp.Port}"
                + (smtp.Username is null ? "" : $" as {smtp.Username}"));
        }

        if (settings.Webhook is { } webhook)
        {
            lines.Add($"{webhook.Url.Host}{webhook.Url.AbsolutePath}");
        }

        return lines;
    }

    public async Task<DeliveryOutcome> SendAsync(
        AlertingSettings settings, Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(notification);

        List<string> delivered = [];
        List<string> failed = [];

        if (settings.Smtp is { } smtp)
        {
            await this.SendMailAsync(smtp, notification, delivered, failed, cancellationToken)
                .ConfigureAwait(false);
        }

        if (settings.Webhook is { } webhook)
        {
            await PostAsync(webhook, notification, delivered, failed, cancellationToken)
                .ConfigureAwait(false);
        }

        return new DeliveryOutcome(delivered, failed);
    }

    private async Task SendMailAsync(
        SmtpSettings smtp,
        Notification notification,
        List<string> delivered,
        List<string> failed,
        CancellationToken cancellationToken)
    {
        string target = string.Join(", ", smtp.To);

        try
        {
            using SmtpClient client = new(smtp.Host, smtp.Port)
            {
                EnableSsl = smtp.StartTls,
                Timeout = (int)timeout.TotalMilliseconds,
                Credentials = this.CredentialsFor(smtp),
            };

            using MailMessage message = new()
            {
                From = new MailAddress(smtp.From),
                Subject = notification.Subject,
                Body = notification.Body,
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8,
            };

            foreach (string recipient in smtp.To)
            {
                message.To.Add(recipient);
            }

            await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
            delivered.Add(target);
        }
        catch (Exception exception) when (
            exception is SmtpException or InvalidOperationException or FormatException
                or IOException or OperationCanceledException)
        {
            failed.Add($"{target} via {smtp.Host}:{smtp.Port}: {exception.Message}");
        }
    }

    /// A named secret the host does not hold is a refusal, not an empty password: an
    /// anonymous attempt against an authenticating relay fails at the far end with a message
    /// nobody can trace back to a missing environment variable.
    private NetworkCredential? CredentialsFor(SmtpSettings smtp)
    {
        if (smtp.Username is null || smtp.PasswordSecret is null)
        {
            return null;
        }

        string password = secrets.Find(smtp.PasswordSecret)
            ?? throw new InvalidOperationException(
                $"no secret named '{smtp.PasswordSecret}' is available to this process");

        return new NetworkCredential(smtp.Username, password);
    }

    private static async Task PostAsync(
        WebhookSettings webhook,
        Notification notification,
        List<string> delivered,
        List<string> failed,
        CancellationToken cancellationToken)
    {
        string target = $"{webhook.Url.Host}{webhook.Url.AbsolutePath}";

        try
        {
            using HttpClient client = new() { Timeout = TransportNotifier.DefaultTimeout };

            using StringContent content = new(
                JsonSerializer.Serialize(new
                {
                    kind = notification.Kind.ToString().ToLowerInvariant(),
                    subject = notification.Subject,
                    body = notification.Body,
                }),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await client
                .PostAsync(webhook.Url, content, cancellationToken)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                delivered.Add(target);
            }
            else
            {
                failed.Add($"{target}: HTTP {(int)response.StatusCode}");
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or OperationCanceledException)
        {
            failed.Add($"{target}: {exception.Message}");
        }
    }
}
