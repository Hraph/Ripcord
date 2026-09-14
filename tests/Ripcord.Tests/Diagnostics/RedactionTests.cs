using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Diagnostics;

/// Non-negotiable rule 7, applied to the one file that is meant to leave the host. A
/// diagnostic log is written to be sent somewhere, so a webhook token in it is a webhook token
/// in a ticket, in a mailbox and in somebody's downloads folder.
public class RedactionTests
{
    /// The whole of a Teams or Slack webhook's authentication is in its path. The host is what
    /// explains the failure and stays; the path is the credential and goes.
    [Theory]
    [InlineData(
        "posting to https://hooks.slack.com/services/T00/B00/XXXXXXXX failed",
        "posting to https://hooks.slack.com/[removed] failed")]
    [InlineData(
        "https://example.webhook.office.com/webhookb2/abc@def/IncomingWebhook/ghi",
        "https://example.webhook.office.com/[removed]")]
    [InlineData("connecting to https://relay.example.com", "connecting to https://relay.example.com")]
    [InlineData("https://relay.example.com/", "https://relay.example.com/")]
    public void A_url_keeps_its_host_and_loses_its_path(string line, string expected) =>
        Assert.Equal(expected, Redaction.Scrub(line));

    /// `user:password@host` is the shape a proxy or an SMTP URL arrives in.
    [Fact]
    public void Credentials_in_front_of_a_host_are_removed()
    {
        Assert.Equal(
            "https://[removed]@relay.example.com/[removed]",
            Redaction.Scrub("https://ripcord:hunter2@relay.example.com/send?to=a"));
    }

    [Theory]
    [InlineData("smtp password=hunter2 rejected", "smtp password=[removed] rejected")]
    [InlineData("{\"token\": \"abc123\"}", "{\"token\": \"[removed]\"}")]
    [InlineData("api_key=abc;host=x", "api_key=[removed];host=x")]
    public void A_value_whose_key_names_a_secret_is_removed(string line, string expected) =>
        Assert.Equal(expected, Redaction.Scrub(line));

    /// The scheme is kept and the credential after it is taken. Stopping at the first space
    /// would remove the word `Bearer` and write the token out in full, which is the leak
    /// wearing the shape of a redaction.
    [Theory]
    [InlineData("Authorization: Bearer abc123", "Authorization: Bearer [removed]")]
    [InlineData("Authorization: Basic dXNlcjpwdw==", "Authorization: Basic [removed]")]
    [InlineData("{\"Authorization\": \"Bearer abc123\"}", "{\"Authorization\": \"Bearer [removed]\"}")]
    public void An_authentication_scheme_is_kept_and_what_follows_it_is_not(
        string line, string expected) =>
        Assert.Equal(expected, Redaction.Scrub(line));

    /// `client_secret` and `ClientSecret` are the same key spelt two ways, and a rule that only
    /// sees word boundaries at punctuation catches one of them.
    [Theory]
    [InlineData("ClientSecret=abcd1234", "ClientSecret=[removed]")]
    [InlineData("client_secret=abcd1234", "client_secret=[removed]")]
    [InlineData("apiToken: abcd1234", "apiToken: [removed]")]
    public void A_key_spelt_in_camel_case_is_still_a_key(string line, string expected) =>
        Assert.Equal(expected, Redaction.Scrub(line));

    /// Hyphens, because that is how a header is spelt.
    [Fact]
    public void A_hyphenated_header_name_is_recognised() =>
        Assert.Equal("X-Api-Key: [removed]", Redaction.Scrub("X-Api-Key: sk_live_000"));

    /// The key has to be a word. A rule that matched anywhere would redact the middle of a VM
    /// name and leave the log lying about what it saw.
    [Theory]
    [InlineData("passwordless sign-in failed")]
    [InlineData("the tokenizer rejected it")]
    public void A_word_that_merely_contains_a_secret_key_is_left_alone(string line) =>
        Assert.Equal(line, Redaction.Scrub(line));

    /// Everything else is a stack trace, a VM name or a CIM error, and all of it is the reason
    /// the file exists.
    [Fact]
    public void Ordinary_text_passes_through_untouched()
    {
        const string Line =
            "Type mismatch for parameter 'ReplicationRelationship' at Ripcord.Adapters.Wmi";

        Assert.Equal(Line, Redaction.Scrub(Line));
    }

    [Fact]
    public void Nothing_is_an_empty_line_rather_than_a_throw() =>
        Assert.Equal("", Redaction.Scrub(null));
}
