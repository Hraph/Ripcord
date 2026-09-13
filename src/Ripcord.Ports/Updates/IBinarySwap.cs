using Ripcord.Domain.Updates;

namespace Ripcord.Ports.Updates;

/// Every move the update makes on this host's filesystem, and nothing else. It does not
/// decide what to move or in what order — `UpdatePlan` settled that — and it does not verify
/// anything.
///
/// It is this thin on purpose: renaming a running `.exe` is the one part of this feature that
/// cannot be exercised anywhere but on Windows, so as little as possible is allowed to live
/// behind it.
public interface IBinarySwap
{
    /// What is already lying beside the running binary — a staged download or a copy an
    /// earlier update set aside.
    StagedBinaries Observe(string binaryPath);

    void Apply(UpdateStep move, StagedRelease release, CancellationToken cancellationToken);

    /// Put the binary that was set aside back. Its own method rather than another step,
    /// because it runs when the sequence has already failed and on its own deadline.
    void Restore(string binaryPath);
}

/// What an earlier run left behind. Both absent is the ordinary state.
public sealed record StagedBinaries(bool HasStagedDownload, bool HasPreviousBinary);

/// The verified bytes, and where they are going. Reaching the swap at all means the signature
/// has already been checked.
public sealed record StagedRelease(string BinaryPath, byte[] Payload, string Version);
