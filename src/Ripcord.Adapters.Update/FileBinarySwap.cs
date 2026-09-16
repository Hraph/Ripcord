using System.Diagnostics;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Updates;

namespace Ripcord.Adapters.Update;

/// The four file moves an update makes, beside the running binary. Nothing here decides
/// anything: the order came from `UpdatePlan`, and the bytes were verified before this type
/// was reached.
///
/// The moves themselves are ordinary file operations and are tested as such. What cannot be
/// exercised off Windows is narrower and worth naming: that a **running** `.exe` can be
/// renamed but not overwritten — which is why the sequence moves the old one aside rather than
/// writing over it — and whether the account the service runs as may write beside the binary
/// at all. Everything that could have been a decision lives in the Domain instead, so those
/// two questions are all that reach a real host untested.
public sealed class FileBinarySwap : IBinarySwap
{
    private const string StagedSuffix = ".new";

    private const string PreviousSuffix = ".old";

    public StagedBinaries Observe(string binaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);

        FileInfo previous = new(Previous(binaryPath));

        return new StagedBinaries(
            File.Exists(Staged(binaryPath)),
            previous.Exists,
            previous.Exists ? VersionOf(previous.FullName) : null,
            previous.Exists ? new DateTimeOffset(previous.LastWriteTimeUtc, TimeSpan.Zero) : null,
            File.Exists(Interrupted(binaryPath)));
    }

    /// Read off the file, not remembered anywhere. A host whose binary was replaced by hand
    /// still answers, and there is no state file to go stale.
    private static string? VersionOf(string path)
    {
        try
        {
            if (FileVersionInfo.GetVersionInfo(path).ProductVersion is not { Length: > 0 } stamped)
            {
                return null;
            }

            // `0.2.1+abc123def456`. The commit is not a version difference, and every other
            // line of this tool shows the part in front of the `+` — a screen that prints one
            // of each makes the reader work out that they are not comparing like with like.
            int commit = stamped.IndexOf('+', StringComparison.Ordinal);

            return commit > 0 ? stamped[..commit] : stamped;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable, or not a binary that carries one. The plan says so and goes on.
            return null;
        }
    }

    public void Apply(UpdateStep move, StagedRelease release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(move);
        ArgumentNullException.ThrowIfNull(release);

        string binary = release.BinaryPath;

        switch (move.Action)
        {
            case UpdateAction.DiscardPrevious:
                File.Delete(Previous(binary));
                break;

            // Written whole, then moved into place, so a download interrupted half way never
            // leaves something that looks like a staged release.
            case UpdateAction.SetAside:
                Stage(binary, release.Payload);
                File.Move(binary, Previous(binary));
                break;

            case UpdateAction.Install:
                File.Move(Staged(binary), binary);
                break;

            default:
                throw new InvalidOperationException(
                    $"{move.Action} is not a move on this host's filesystem");
        }
    }

    /// Called when the swap has already failed, so it must work from whatever state that
    /// left. A staged file is removed if the old binary went back: keeping it would offer the
    /// next run a release nothing re-verified.
    public void Restore(string binaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);

        if (!File.Exists(binaryPath) && File.Exists(Previous(binaryPath)))
        {
            File.Move(Previous(binaryPath), binaryPath);
        }

        File.Delete(Staged(binaryPath));
    }

    /// The exchange, arranged so that the window in which this host has no binary is two
    /// renames wide and nothing else.
    ///
    /// The kept binary is **copied** into place first, before anything is moved: a copy
    /// touches nothing that exists, so everything that can fail for want of room or
    /// permission fails while the host is still whole. Only then are the two renames made,
    /// and if the second one fails the first is undone.
    ///
    /// `.swap` is never overwritten. A file of that name is a previous exchange that did not
    /// finish, and it holds a binary this host was running — writing over it would destroy the
    /// only copy of it, silently, at the worst possible moment.
    public SwapOutcome SwapWithPrevious(string binaryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryPath);

        string aside = Interrupted(binaryPath);
        string incoming = binaryPath + ".incoming";

        try
        {
            if (File.Exists(aside))
            {
                throw new IOException(
                    $"'{aside}' is a binary left by an exchange that did not finish; "
                    + "move it somewhere safe before running this again");
            }

            File.Copy(Previous(binaryPath), incoming, overwrite: true);
        }
        catch (Exception exception) when (IsFilesystem(exception))
        {
            Discard(incoming);
            throw;
        }

        // From here the host is briefly without a binary at the path the service starts from.
        File.Move(binaryPath, aside);

        try
        {
            File.Move(incoming, binaryPath);
        }
        catch (Exception exception) when (IsFilesystem(exception))
        {
            _ = exception;
            return PutBack(binaryPath, aside, incoming);
        }

        // The running binary is in place; what is left is bookkeeping. A failure here leaves a
        // host that runs, so it is not an incomplete exchange — only an untidy directory.
        try
        {
            File.Move(aside, Previous(binaryPath), overwrite: true);
        }
        catch (Exception exception) when (IsFilesystem(exception))
        {
            _ = exception;
        }

        return SwapOutcome.Exchanged;
    }

    /// The running binary goes back where it was. Reported rather than thrown: whether this
    /// host still has something to start is the only question worth answering afterwards.
    private static SwapOutcome PutBack(string binaryPath, string aside, string incoming)
    {
        try
        {
            File.Move(aside, binaryPath);
            Discard(incoming);

            return SwapOutcome.Recovered;
        }
        catch (Exception exception) when (IsFilesystem(exception))
        {
            _ = exception;
            return SwapOutcome.LeftIncomplete;
        }
    }

    private static void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (IsFilesystem(exception))
        {
            // A file left behind is untidy; failing here would replace a tidy-up with an
            // error about the tidy-up.
            _ = exception;
        }
    }

    private static bool IsFilesystem(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException;

    private static string Interrupted(string binaryPath) => binaryPath + ".swap";

    private static void Stage(string binaryPath, byte[] payload)
    {
        string staged = Staged(binaryPath);
        string writing = staged + ".partial";

        File.WriteAllBytes(writing, payload);
        File.Move(writing, staged, overwrite: true);
    }

    private static string Staged(string binaryPath) => binaryPath + StagedSuffix;

    private static string Previous(string binaryPath) => binaryPath + PreviousSuffix;
}
