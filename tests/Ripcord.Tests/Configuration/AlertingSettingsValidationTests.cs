using Ripcord.Domain.Alerting;
using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Configuration;

/// The `alerting` block. Absent on a host that notifies nobody, which stays the default:
/// these two machines are meant to have no outbound access at all.
public class AlertingSettingsValidationTests
{
    [Fact]
    public void A_document_with_no_alerting_block_notifies_nobody()
    {
        RipcordConfiguration configuration = Validated(document => document.Alerting = null);

        Assert.False(configuration.Alerting.Enabled);
        Assert.Null(configuration.Alerting.Smtp);
        Assert.Null(configuration.Alerting.Webhook);
    }

    [Fact]
    public void A_complete_smtp_block_is_read()
    {
        AlertingSettings alerting = Validated(Alerting).Alerting;

        Assert.True(alerting.Enabled);
        Assert.Equal(TimeSpan.FromHours(6), alerting.RepeatAfter);
        Assert.Equal("22:00-07:00", alerting.QuietHours!.ToString());
        Assert.Equal("smtp.example.net", alerting.Smtp!.Host);
        Assert.Equal(587, alerting.Smtp.Port);
        Assert.Equal(["ops@example.net"], alerting.Smtp.To);
        Assert.Equal("RIPCORD_SMTP_PASSWORD", alerting.Smtp.PasswordSecret);
    }

    [Fact]
    public void A_webhook_url_is_read()
    {
        AlertingSettings alerting = Validated(document => document.Alerting = new AlertingDocument
        {
            Enabled = true,
            Webhook = new WebhookDocument { Url = "https://hooks.example.net/ripcord" },
        }).Alerting;

        Assert.Equal(new Uri("https://hooks.example.net/ripcord"), alerting.Webhook!.Url);
    }

    /// Enabled with nowhere to send is the failure that is invisible until the night it
    /// matters: every run decides to notify, and nothing ever arrives.
    [Fact]
    public void Alerting_enabled_with_no_transport_is_refused()
    {
        Assert.Equal(
            "alerting",
            Refused(document => document.Alerting = new AlertingDocument { Enabled = true }).Path);
    }

    /// The whole block is checked whether or not it is switched on, so switching it on is not
    /// the moment the typos surface.
    [Theory]
    [InlineData("alerting.smtp.host", "")]
    [InlineData("alerting.smtp.from", "from")]
    [InlineData("alerting.smtp.to", "to")]
    public void An_incomplete_smtp_block_is_refused(string path, string field)
    {
        ConfigurationError error = Refused(document =>
        {
            Alerting(document);
            document.Alerting!.Enabled = false;

            switch (field)
            {
                case "from": document.Alerting.Smtp!.From = null; break;
                case "to": document.Alerting.Smtp!.To = []; break;
                default: document.Alerting.Smtp!.Host = "  "; break;
            }
        });

        Assert.Equal(path, error.Path);
    }

    [Fact]
    public void An_smtp_port_outside_the_range_is_refused() =>
        Assert.Equal(
            "alerting.smtp.port",
            Refused(document =>
            {
                Alerting(document);
                document.Alerting!.Smtp!.Port = 70_000;
            }).Path);

    /// Decision: the SMTP password is named here and stored elsewhere. A relay password in
    /// `ripcord.yaml` is a password in every backup of the configuration directory.
    [Fact]
    public void A_password_written_into_the_configuration_file_is_refused()
    {
        ConfigurationError error = Refused(document =>
        {
            Alerting(document);
            document.Alerting!.Smtp!.Password = "hunter2";
        });

        Assert.Equal("alerting.smtp.password", error.Path);
        Assert.Contains("password_secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_user_name_with_no_secret_to_go_with_it_is_refused() =>
        Assert.Equal(
            "alerting.smtp.password_secret",
            Refused(document =>
            {
                Alerting(document);
                document.Alerting!.Smtp!.PasswordSecret = null;
            }).Path);

    [Fact]
    public void A_secret_with_no_user_name_to_go_with_it_is_refused() =>
        Assert.Equal(
            "alerting.smtp.username",
            Refused(document =>
            {
                Alerting(document);
                document.Alerting!.Smtp!.Username = null;
            }).Path);

    /// An unauthenticated relay is a real deployment — an internal MTA that accepts from this
    /// subnet and nothing else — so neither field is required, only the pair of them.
    [Fact]
    public void A_relay_that_takes_no_credentials_is_accepted()
    {
        AlertingSettings alerting = Validated(document =>
        {
            Alerting(document);
            document.Alerting!.Smtp!.Username = null;
            document.Alerting.Smtp.PasswordSecret = null;
        }).Alerting;

        Assert.Null(alerting.Smtp!.Username);
    }

    [Theory]
    [InlineData("http://hooks.example.net/ripcord")]
    [InlineData("hooks.example.net")]
    [InlineData("")]
    public void A_webhook_url_that_is_not_https_is_refused(string url) =>
        Assert.Equal(
            "alerting.webhook.url",
            Refused(document => document.Alerting = new AlertingDocument
            {
                Enabled = true,
                Webhook = new WebhookDocument { Url = url },
            }).Path);

    [Fact]
    public void A_quiet_window_that_cannot_be_read_is_refused() =>
        Assert.Equal(
            "alerting.quiet_hours",
            Refused(document =>
            {
                Alerting(document);
                document.Alerting!.QuietHours = "22h-7h";
            }).Path);

    [Fact]
    public void The_repeat_threshold_defaults_to_a_day()
    {
        AlertingSettings alerting = Validated(document =>
        {
            Alerting(document);
            document.Alerting!.RepeatAfterHours = null;
        }).Alerting;

        Assert.Equal(AlertingSettings.DefaultRepeatAfter, alerting.RepeatAfter);
    }

    /// Zero would mean a notification on every scheduled run, which is the behaviour this
    /// whole milestone exists to avoid.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(9_000)]
    public void A_repeat_threshold_outside_the_range_is_refused(int hours) =>
        Assert.Equal(
            "alerting.repeat_after_hours",
            Refused(document =>
            {
                Alerting(document);
                document.Alerting!.RepeatAfterHours = hours;
            }).Path);

    /// Credentials over a connection that never starts TLS are credentials on the wire. The
    /// relay being in the same rack is the argument that holds until it does not.
    [Fact]
    public void A_relay_given_credentials_without_start_tls_is_refused()
    {
        ConfigurationError error = Refused(document =>
        {
            Alerting(document);
            document.Alerting!.Smtp!.StartTls = false;
        });

        Assert.Equal("alerting.smtp.start_tls", error.Path);
    }

    /// Without credentials there is nothing to leak by authenticating, so an unauthenticated
    /// relay on a plain connection stays sayable.
    [Fact]
    public void A_relay_with_no_credentials_may_run_without_start_tls()
    {
        AlertingSettings alerting = Validated(document =>
        {
            Alerting(document);
            document.Alerting!.Smtp!.StartTls = false;
            document.Alerting.Smtp.Username = null;
            document.Alerting.Smtp.PasswordSecret = null;
        }).Alerting;

        Assert.False(alerting.Smtp!.StartTls);
    }

    private static void Alerting(ConfigurationDocument document) =>
        document.Alerting = new AlertingDocument
        {
            Enabled = true,
            RepeatAfterHours = 6,
            QuietHours = "22:00-07:00",
            Smtp = new SmtpDocument
            {
                Host = "smtp.example.net",
                Port = 587,
                StartTls = true,
                From = "ripcord@example.net",
                To = ["ops@example.net"],
                Username = "ripcord@example.net",
                PasswordSecret = "RIPCORD_SMTP_PASSWORD",
            },
        };

    private static RipcordConfiguration Validated(Action<ConfigurationDocument> adjust) =>
        Configurations.Create(adjust);

    private static ConfigurationError Refused(Action<ConfigurationDocument> adjust)
    {
        ConfigurationDocument document = ValidDocument.Create();
        adjust(document);

        ConfigurationValidation validation =
            ConfigurationValidator.Validate(document, ValidDocument.MachineName);

        Assert.Null(validation.Configuration);
        return Assert.Single(validation.Errors);
    }
}
