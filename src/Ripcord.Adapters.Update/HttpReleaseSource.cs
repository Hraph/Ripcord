using System.Globalization;
using System.Net.Http.Headers;
using Ripcord.Ports.Updates;

namespace Ripcord.Adapters.Update;

/// Fetches the published binary and the signature beside it, from the release the feed named.
///
/// It never throws and it never decides. It does enforce one thing the Domain cannot: a
/// **size cap**, because an unbounded body streamed into memory on a host that is about to
/// perform a failover is a denial of service wearing a release's clothes.
///
/// The repository is addressed by numeric id for the same reason the feed does it: a rename
/// leaves a redirect that keeps working right up to the moment somebody claims the abandoned
/// name, at which point the old URL starts returning a stranger's releases, successfully.
public sealed class HttpReleaseSource(
    long repositoryId,
    string userAgent,
    TimeSpan timeout,
    long maximumBytes,
    Uri? releaseHost = null)
    : IReleaseSource
{
    private static readonly Uri GitHub = new("https://github.com/");

    /// A self-contained single-file win-x64 build is around 70 MB. Double it, so a legitimate
    /// release has room to grow and nothing else does.
    public const long DefaultMaximumBytes = 160L * 1024 * 1024;

    public async Task<FetchedRelease> FetchAsync(
        string version, CancellationToken cancellationToken)
    {
        try
        {
            using HttpClient client = new() { Timeout = timeout };

            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/octet-stream"));

            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

            byte[]? payload = await this
                .DownloadAsync(client, version, "ripcord.exe", cancellationToken)
                .ConfigureAwait(false);

            if (payload is null)
            {
                return FetchedRelease.Failed($"the release binary for {version} could not be read");
            }

            byte[]? signature = await this
                .DownloadAsync(client, version, "ripcord.exe.sig", cancellationToken)
                .ConfigureAwait(false);

            // A release published without its signature is not a release this binary can
            // install. Said here rather than left to look like a network failure.
            return signature is null
                ? FetchedRelease.Failed(
                    $"the release {version} carries no signature file; it cannot be verified "
                    + "and will not be installed")
                : FetchedRelease.Fetched(payload, signature);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or OperationCanceledException or IOException)
        {
            return FetchedRelease.Failed($"the release could not be fetched: {exception.Message}");
        }
    }

    private async Task<byte[]?> DownloadAsync(
        HttpClient client, string version, string asset, CancellationToken cancellationToken)
    {
        // The tag comes off the GitHub API, so it is escaped rather than trusted to be a
        // version number. `UpdatePlan` already halts on a tag that will not parse as one, and
        // this is the second answer to the same question: a path segment is a path segment.
        Uri endpoint = new(
            releaseHost ?? GitHub,
            string.Create(
                CultureInfo.InvariantCulture,
                $"repositories/{repositoryId}/releases/download/{Uri.EscapeDataString(version)}/{asset}"));

        using HttpResponseMessage response = await client
            .GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        // Checked before reading and again while reading: the declared length is a hint, and a
        // response that lies about it must still not be allowed to fill memory.
        if (response.Content.Headers.ContentLength is { } declared && declared > maximumBytes)
        {
            return null;
        }

        using Stream body = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        return await ReadCappedAsync(body, maximumBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]?> ReadCappedAsync(
        Stream body, long maximum, CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81_920];

        while (true)
        {
            int read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximum)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }
    }
}
