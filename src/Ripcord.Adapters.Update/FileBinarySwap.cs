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

        return new StagedBinaries(
            File.Exists(Staged(binaryPath)), File.Exists(Previous(binaryPath)));
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
