using System.Net.Http.Headers;
using System.Text.Json;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Updates;

namespace Ripcord.Adapters.Update;

/// The latest published release, read from the GitHub API. It reads a version string and stops
/// there — downloading and installing are `ripcord update`, behind a second switch and a
/// signature check — so the worst *this* can produce is a wrong version number.
///
/// **The repository is addressed by numeric id, never by `owner/name`.** A rename leaves a
/// permanent redirect that HttpClient follows, so the obvious form keeps working right up to
/// the moment somebody creates a repository under the abandoned name — at which point the old
/// URL stops failing and starts returning a stranger's releases, successfully. The id is
/// immutable, survives a rename and a change of owner, and a recreated `Hraph/Ripcord` gets a
/// different one that this binary never sees.
public sealed class GitHubReleaseFeed(long repositoryId, string userAgent, TimeSpan timeout)
    : IReleaseFeed
{
    public async Task<ReleaseLookup> LatestAsync(CancellationToken cancellationToken)
    {
        Uri endpoint = new($"https://api.github.com/repositories/{repositoryId}/releases/latest");

        try
        {
            using HttpClient client = new() { Timeout = timeout };

            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            // The API refuses a request with no user agent, and a named one is what appears in
            // the repository's traffic log when somebody asks who is polling it.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);

            using HttpResponseMessage response = await client
                .GetAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return ReleaseLookup.Failed(
                    $"api.github.com answered HTTP {(int)response.StatusCode} for the release feed");
            }

            using JsonDocument payload = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            return payload.RootElement.TryGetProperty("tag_name", out JsonElement tag)
                && tag.GetString() is { Length: > 0 } version
                ? ReleaseLookup.Found(version)
                : ReleaseLookup.Failed("the release feed named no version");
        }
        catch (Exception exception) when (
            exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            return ReleaseLookup.Failed($"the release feed could not be read: {exception.Message}");
        }
    }
}
