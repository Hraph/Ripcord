using System.Text;

namespace Ripcord.Domain.Diagnostics;

/// Removes from a diagnostic line the things non-negotiable rule 7 says never appear in a log.
///
/// The log exists to be sent somewhere — pasted into a ticket, attached to a mail, read by
/// whoever is helping. That is exactly what makes it the wrong place for a webhook URL: the
/// token is the whole of the authentication, it sits in the path rather than in a header, and
/// a `HttpRequestException` puts the request URI in its own message without being asked.
///
/// Two shapes, because two are what can reach here:
///
/// - a URL, cut down to scheme and host. The host is the half that explains a failure — name
///   resolution, a refused connection, a proxy — and the path is the half that is a secret.
/// - a `key=value` whose key names a secret, in a connection string or a query.
///
/// It scrubs rather than refuses. A line that cannot be written is a line nobody can read.
public static class Redaction
{
    /// What is left where a secret was. Distinctive enough to be searched for, so a reader
    /// asking "was something removed here?" has an answer.
    public const string Removed = "[removed]";

    private static readonly string[] SecretKeys =
    [
        "password", "passwd", "pwd", "secret", "token",
        "apikey", "api_key", "api-key", "authorization",
    ];

    /// What an `Authorization` header puts in front of the credential. Kept, and the token
    /// after it taken: `Authorization: [removed]` loses the one part of that header worth
    /// reading, and stopping at the first space loses the whole point of removing it.
    private static readonly string[] AuthenticationSchemes =
        ["bearer", "basic", "digest", "token", "negotiate", "ntlm"];

    private static readonly string[] Schemes = ["http://", "https://"];

    public static string Scrub(string? text)
    {
        if (text is null || text.Length == 0)
        {
            return "";
        }

        return ScrubKeys(ScrubUrls(text));
    }

    /// Everything from the scheme up to the end of the authority is kept; the path, the query
    /// and any `user:password@` in front of the host go.
    private static string ScrubUrls(string text)
    {
        StringBuilder? scrubbed = null;
        int copied = 0;

        for (int index = 0; index < text.Length; index++)
        {
            if (SchemeAt(text, index) is not { } scheme)
            {
                continue;
            }

            int authority = index + scheme.Length;
            int end = EndOfUrl(text, authority);

            scrubbed ??= new StringBuilder(text.Length);
            scrubbed.Append(text, copied, index - copied).Append(scheme);

            string host = text[authority..end];
            int authorityEnd = host.AsSpan().IndexOfAny('/', '?');

            // Only in front of the host. A Teams webhook carries an `@` in the middle of its
            // path, and reading that as credentials would keep the secret and cut the host.
            int credentials = host.AsSpan(0, authorityEnd < 0 ? host.Length : authorityEnd)
                .LastIndexOf('@');

            if (credentials >= 0)
            {
                scrubbed.Append(Removed).Append('@');
                host = host[(credentials + 1)..];
                authorityEnd = host.AsSpan().IndexOfAny('/', '?');
            }

            if (authorityEnd < 0)
            {
                scrubbed.Append(host);
            }
            else
            {
                // The separator is kept and what follows is said rather than silently cut: a
                // reader comparing this against a configuration has to know a path was there.
                scrubbed.Append(host, 0, authorityEnd).Append(host[authorityEnd]);

                if (authorityEnd < host.Length - 1)
                {
                    scrubbed.Append(Removed);
                }
            }

            copied = end;
            index = end - 1;
        }

        if (scrubbed is null)
        {
            return text;
        }

        return scrubbed.Append(text, copied, text.Length - copied).ToString();
    }

    /// A URL ends at whitespace or at a quote. Nothing else is assumed: a trailing bracket or
    /// full stop staying inside the host is a cosmetic flaw, and guessing further would start
    /// cutting real host names in half.
    private static int EndOfUrl(string text, int from)
    {
        for (int index = from; index < text.Length; index++)
        {
            if (char.IsWhiteSpace(text[index]) || text[index] is '"' or '\'')
            {
                return index;
            }
        }

        return text.Length;
    }

    private static string? SchemeAt(string text, int index) =>
        Schemes.FirstOrDefault(scheme =>
            string.CompareOrdinal(text, index, scheme, 0, scheme.Length) == 0);

    /// `password=...` up to the next separator. Applied after the URLs, so a query string that
    /// survived as a bare fragment is still covered.
    private static string ScrubKeys(string text)
    {
        foreach (string key in SecretKeys)
        {
            int index;
            int from = 0;

            while ((index = text.IndexOf(key, from, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                int separator = SeparatorAfter(text, index + key.Length);

                if (separator < 0 || !StartsWord(text, index))
                {
                    from = index + key.Length;
                    continue;
                }

                int value = StartOfValue(text, separator + 1);
                int end = EndOfValue(text, value);

                text = end > value ? text[..value] + Removed + text[end..] : text;
                from = value + Removed.Length;
            }
        }

        return text;
    }

    /// The key has to be a word of its own: `passwordless` is not a password, and neither is
    /// the `token` inside `tokenizer`.
    ///
    /// A lower-to-upper transition counts as a boundary, because `ClientSecret` is how half
    /// the world spells the thing `client_secret` spells with an underscore, and only one of
    /// the two would otherwise be caught.
    private static bool StartsWord(string text, int index) =>
        index == 0
        || !char.IsLetterOrDigit(text[index - 1])
        || (char.IsLower(text[index - 1]) && char.IsUpper(text[index]));

    private static int SeparatorAfter(string text, int from)
    {
        int index = from;

        while (index < text.Length && (text[index] == ' ' || text[index] == '"'))
        {
            index++;
        }

        return index < text.Length && text[index] is '=' or ':' ? index : -1;
    }

    /// The quotes and the spacing around a value are kept, so a JSON line still reads as JSON
    /// and a reader can see the shape of what was taken out. An authentication scheme in front
    /// of the credential is kept for the same reason — and skipped over, so that what is
    /// removed is the credential rather than the word `Bearer`.
    private static int StartOfValue(string text, int from)
    {
        int index = SkipSpacing(text, from);
        int afterScheme = SkipSpacing(text, EndOfValue(text, index));

        return afterScheme > index && IsScheme(text[index..EndOfValue(text, index)])
            ? afterScheme
            : index;
    }

    private static int SkipSpacing(string text, int from)
    {
        int index = from;

        while (index < text.Length && (text[index] == ' ' || text[index] == '"'))
        {
            index++;
        }

        return index;
    }

    private static bool IsScheme(string word) =>
        AuthenticationSchemes.Contains(word, StringComparer.OrdinalIgnoreCase);

    private static int EndOfValue(string text, int from)
    {
        for (int index = from; index < text.Length; index++)
        {
            if (char.IsWhiteSpace(text[index]) || text[index] is ';' or '&' or '"' or ',')
            {
                return index;
            }
        }

        return text.Length;
    }
}
