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

    /// Exchange the running binary with the one set aside, for `ripcord rollback`.
    ///
    /// Distinct from `Restore`, which only acts when the running binary has gone missing —
    /// that is a failed swap being undone. This one runs when everything is where it should
    /// be and the operator has asked to go back anyway.
    ///
    /// An exchange rather than a replacement because a running binary can be renamed and not
    /// deleted: every move here is a rename, and the version being left behind becomes the one
    /// set aside.
    ///
    /// It reports how far it got, and that is not decoration. There is a window — short, but
    /// real — in which this host has no binary at the path the service starts from, and
    /// "nothing moved" and "moved part way and could not be put back" are the difference
    /// between re-running the command and somebody driving to the site.
    SwapOutcome SwapWithPrevious(string binaryPath);
}

/// How far the exchange got.
public enum SwapOutcome
{
    /// It failed before the running binary was touched. The host is where it was.
    NothingMoved,

    Exchanged,

    /// It failed part way and the running binary was put back. The host is where it was, and
    /// the reason it could not go back is worth reading.
    Recovered,

    /// It failed part way and could **not** be put back. This host may have no binary where
    /// the service starts from, and a human has to look.
    LeftIncomplete,
}

/// What an earlier run left behind. Both absent is the ordinary state.
///
/// The version and the date are read off the file rather than recorded anywhere, so a host
/// that was updated by hand still answers. Either may be null — a file that does not carry a
/// version is a fact about the file, never a reason to refuse to go back to it.
public sealed record StagedBinaries(
    bool HasStagedDownload,
    bool HasPreviousBinary,
    string? PreviousVersion = null,
    DateTimeOffset? PreviousSetAsideAt = null,
    /// A file left by an exchange that did not finish. It holds a binary this host was
    /// running, so the next exchange refuses rather than writing over it.
    bool HasInterruptedSwap = false);

/// The verified bytes, and where they are going. Reaching the swap at all means the signature
/// has already been checked.
public sealed record StagedRelease(string BinaryPath, byte[] Payload, string Version);
