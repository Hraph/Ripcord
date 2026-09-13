using Ripcord.Domain.Updates;

namespace Ripcord.Ports.Updates;

/// Reads the latest published release, and nothing else. It never downloads and never
/// installs (milestone 5): the exposure is bounded to a wrong version number, and that bound
/// is a decision rather than an accident.
///
/// It never throws. A host with no outbound access is the normal case here, and a DNS failure
/// is an answer to report, not an exception.
public interface IReleaseFeed
{
    Task<ReleaseLookup> LatestAsync(CancellationToken cancellationToken);
}
