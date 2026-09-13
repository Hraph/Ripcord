using Ripcord.Domain.Updates;

namespace Ripcord.Ports.Updates;

/// Fetches a published release and the signature beside it. It never throws — a host with no
/// outbound access is the normal case here — and it never decides: whether the bytes it
/// returned are genuine is `ReleaseSignature`'s answer, in the Domain.
///
/// The bytes come back in memory rather than as a path, so nothing is written beside the
/// running binary until after the signature has been checked.
public interface IReleaseSource
{
    Task<FetchedRelease> FetchAsync(string version, CancellationToken cancellationToken);
}

/// Either a release or the reason there is none. A fetch that failed is never an empty
/// release: "nothing was published" and "nothing could be read" are different claims.
public sealed record FetchedRelease(
    byte[]? Payload, byte[]? Signature, string? FailureMessage)
{
    public static FetchedRelease Fetched(byte[] payload, byte[] signature) =>
        new(payload, signature, null);

    public static FetchedRelease Failed(string message) => new(null, null, message);

    public bool Arrived => this.Payload is not null && this.Signature is not null;
}
