using Ripcord.Domain.Updates;
using Ripcord.Ports.Updates;

namespace Ripcord.Tests.Updates;

/// The release feed without the network. Also the default in the CLI tests, where it stands
/// for a host that cannot reach GitHub — which is the normal state of both real ones.
internal sealed class StubReleaseFeed(ReleaseLookup lookup) : IReleaseFeed
{
    public static StubReleaseFeed Unreachable() =>
        new(ReleaseLookup.Failed("api.github.com could not be resolved"));

    public static StubReleaseFeed Publishing(string version) =>
        new(ReleaseLookup.Found(version));

    public Task<ReleaseLookup> LatestAsync(CancellationToken cancellationToken) =>
        Task.FromResult(lookup);
}
