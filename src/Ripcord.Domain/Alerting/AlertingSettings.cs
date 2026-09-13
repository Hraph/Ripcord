namespace Ripcord.Domain.Alerting;

/// Where a notification goes, and how often. Disabled is the default and a first-class
/// state: a host with no mail relay must run `ripcord check` exactly as it does today.
public sealed record AlertingSettings(
    bool Enabled,
    TimeSpan RepeatAfter,
    QuietHours? QuietHours,
    SmtpSettings? Smtp,
    WebhookSettings? Webhook)
{
    /// A day. Short enough that a fortnight-long resync is mentioned again, long enough that
    /// nobody writes a filter rule for it.
    public static readonly TimeSpan DefaultRepeatAfter = TimeSpan.FromHours(24);

    public static AlertingSettings Disabled() =>
        new(false, DefaultRepeatAfter, null, null, null);
}

/// The relay. The password is never here: `PasswordSecret` names it, and the host resolves
/// the name against something that is not a file in the configuration directory.
public sealed record SmtpSettings(
    string Host,
    int Port,
    bool StartTls,
    string From,
    IReadOnlyList<string> To,
    string? Username,
    string? PasswordSecret);

public sealed record WebhookSettings(Uri Url);
