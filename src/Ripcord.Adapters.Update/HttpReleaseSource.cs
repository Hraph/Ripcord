using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Updates;

namespace Ripcord.Adapters.Update;

/// Where releases are read from. One host, because everything is asked of the API — the
/// assets included.
public sealed record ReleaseOrigin(Uri Api)
{
    public static ReleaseOrigin GitHub { get; } = new(new Uri("https://api.github.com/"));
}

/// Fetches the published binary and the signature beside it.
///
/// **Nothing here is addressed by name, and no address out of a response body is followed.**
/// `github.com/repositories/{id}/...` is not a route at all — the numeric repository id is an
/// API path, never a web one — so the URL this used to build returned 404 for every release on
/// every host. The API is asked instead, by id, for the release carrying the tag; from its
/// answer only the asset **ids** are read, integers, and the download address is then built
/// from two numbers this tool already holds. `browser_download_url` would have worked and
/// carries `Hraph/Ripcord` in it; asking by id means the repository name is never typed, never
/// followed, and never able to go stale.
///
/// It never throws and it never decides whether the bytes are genuine — `ReleaseSignature`
/// answers that, in the Domain. It does enforce the one thing the Domain cannot: a size cap,
/// because an unbounded body streamed into memory on a host about to perform a failover is a
/// denial of service wearing a release's clothes.
public sealed class HttpReleaseSource(
    long repositoryId,
    string userAgent,
    TimeSpan timeout,
    long maximumBytes,
    ReleaseOrigin? origin = null)
    : IReleaseSource
{
    /// A self-contained single-file win-x64 build is around 70 MB. Double it, so a legitimate
    /// release has room to grow and nothing else does.
    public const long DefaultMaximumBytes = 160L * 1024 * 1024;

    private ReleaseOrigin Origin => origin ?? ReleaseOrigin.GitHub;

    public async Task<FetchedRelease> FetchAsync(
        string version, CancellationToken cancellationToken)
    {
        try
        {
            using HttpClient client = new() { Timeout = timeout };

            // The API refuses a request with no user agent, and a named one is what appears
            // in the repository's traffic log when somebody asks who is pulling releases.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

            IReadOnlyDictionary<string, long>? assets = await this
                .ReadAssetsAsync(client, version, cancellationToken)
                .ConfigureAwait(false);

            if (assets is null)
            {
                return FetchedRelease.Failed(
                    $"the release {version} could not be read from the repository");
            }

            // Refused by name rather than left to look like a network failure: a release
            // published without its signature cannot be verified, so it cannot be installed.
            if (ReleaseAssets.MissingFrom(assets.Keys) is { Count: > 0 } missing)
            {
                return FetchedRelease.Failed(
                    $"the release {version} does not carry {string.Join(" or ", missing)}; "
                    + "it cannot be verified and will not be installed");
            }

            byte[]? payload = await this
                .DownloadAsync(client, assets, ReleaseAssets.Binary, cancellationToken)
                .ConfigureAwait(false);

            byte[]? signature = payload is null
                ? null
                : await this
                    .DownloadAsync(client, assets, ReleaseAssets.Signature, cancellationToken)
                    .ConfigureAwait(false);

            return payload is null || signature is null
                ? FetchedRelease.Failed($"the release {version} could not be downloaded")
                : FetchedRelease.Fetched(payload, signature);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or OperationCanceledException
                or IOException
                or JsonException)
        {
            return FetchedRelease.Failed($"the release could not be fetched: {exception.Message}");
        }
    }

    /// The release carrying this exact tag, never "latest". The tag was settled when the
    /// version was checked, and a release published in between must not be the one installed.
    private async Task<IReadOnlyDictionary<string, long>?> ReadAssetsAsync(
        HttpClient client, string version, CancellationToken cancellationToken)
    {
        Uri endpoint = new(
            this.Origin.Api,
            string.Create(
                CultureInfo.InvariantCulture,
                $"repositories/{repositoryId}/releases/tags/{Uri.EscapeDataString(version)}"));

        using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using HttpResponseMessage response = await client
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        using JsonDocument payload = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        if (!payload.RootElement.TryGetProperty("assets", out JsonElement assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // Case-insensitive, to agree with `ReleaseAssets.MissingFrom`: a map that said
        // nothing was missing and then failed to find it would be the worst of both.
        Dictionary<string, long> published = new(StringComparer.OrdinalIgnoreCase);

        // Only the name and the id are read. Everything else in that body, the download URLs
        // included, is somebody else's text.
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out JsonElement name)
                && asset.TryGetProperty("id", out JsonElement id)
                && name.GetString() is { Length: > 0 } key
                && id.TryGetInt64(out long assetId))
            {
                published[key] = assetId;
            }
        }

        return published;
    }

    /// Built from two ids, so the request carries no name and nothing from the response body.
    /// `Accept: application/octet-stream` is what makes the API answer with the bytes rather
    /// than with a description of them.
    private async Task<byte[]?> DownloadAsync(
        HttpClient client,
        IReadOnlyDictionary<string, long> assets,
        string asset,
        CancellationToken cancellationToken)
    {
        if (!assets.TryGetValue(asset, out long assetId))
        {
            return null;
        }

        Uri endpoint = new(
            this.Origin.Api,
            string.Create(
                CultureInfo.InvariantCulture,
                $"repositories/{repositoryId}/releases/assets/{assetId}"));

        using HttpRequestMessage request = new(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        // Checked before reading and again while reading: the declared length is a hint from
        // the same server that is sending the body.
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
