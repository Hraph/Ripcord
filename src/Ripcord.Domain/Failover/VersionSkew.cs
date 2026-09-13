namespace Ripcord.Domain.Failover;

public enum VersionSkewVerdict
{
    Same,

    /// The two hosts run different binaries. Blocks every cross-host sequence.
    Different,

    /// One of the two could not be established — no peer snapshot, or a build that cannot
    /// name itself. Its own answer, never folded into Same: "cannot be shown to match" and
    /// "matches" are different claims, and only one of them is safe to act on.
    NotComparable,
}

/// Whether both hosts are running the same Ripcord. The sequences are encoded in the binary
/// and they span two hosts, so a failback executed half by 0.3.0 and half by 0.4.0 is the
/// error class that cannot be recovered from at 3 a.m.
///
/// This type decides only whether the two builds are the same. **Whether a given verdict
/// blocks is the calling operation's decision**, because the answer differs by operation: a
/// planned failover has a live peer and no excuse for skew, while an unplanned one is defined
/// by the peer being gone, and refusing it for want of a version string would fail at the one
/// thing the tool exists for.
public sealed record VersionSkew(VersionSkewVerdict Verdict, string Explanation)
{
    public static VersionSkew Between(BuildIdentity local, BuildIdentity? peer)
    {
        ArgumentNullException.ThrowIfNull(local);

        if (peer is null)
        {
            return new VersionSkew(
                VersionSkewVerdict.NotComparable,
                $"this host runs {local}; the peer published no build to compare it against");
        }

        if (!local.IsIdentified || !peer.IsIdentified)
        {
            return new VersionSkew(
                VersionSkewVerdict.NotComparable,
                $"this host reports {local} and the peer {peer}; a build that cannot name "
                + "itself cannot be shown to match");
        }

        bool same = string.Equals(local.Version, peer.Version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(local.CommitHash, peer.CommitHash, StringComparison.OrdinalIgnoreCase);

        return same
            ? new VersionSkew(
                VersionSkewVerdict.Same, $"both hosts run {local}")
            : new VersionSkew(
                VersionSkewVerdict.Different,
                $"this host runs {local} and the peer runs {peer}, so a sequence spanning both "
                + "would be executed half by each");
    }
}
