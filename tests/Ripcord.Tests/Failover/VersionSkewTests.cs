using Ripcord.Domain.Failover;

namespace Ripcord.Tests.Failover;

/// The sequences are encoded in the binary and they span two hosts. If one side runs 0.3.0 and
/// the other 0.4.0, a failback executes half a sequence written by one version and half by
/// another — the error class that cannot be recovered from at 3 a.m. So a mismatch blocks
/// rather than warns.
public class VersionSkewTests
{
    private static readonly BuildIdentity Local = new("0.4.0", "abc123");

    [Fact]
    public void The_same_version_and_commit_match()
    {
        Assert.Equal(
            VersionSkewVerdict.Same,
            VersionSkew.Between(Local, new BuildIdentity("0.4.0", "abc123")).Verdict);
    }

    [Fact]
    public void A_different_version_is_skew()
    {
        Assert.Equal(
            VersionSkewVerdict.Different,
            VersionSkew.Between(Local, new BuildIdentity("0.3.0", "abc123")).Verdict);
    }

    /// Same version number, different build. The version alone is not the identity: two
    /// binaries built from different commits at the same version differ in exactly the way
    /// nobody thinks to check.
    [Fact]
    public void The_same_version_from_a_different_commit_is_still_skew()
    {
        Assert.Equal(
            VersionSkewVerdict.Different,
            VersionSkew.Between(Local, new BuildIdentity("0.4.0", "def456")).Verdict);
    }

    /// No peer snapshot at all. This is the defining circumstance of an unplanned failover —
    /// the primary is gone — so it is reported as its own answer rather than as a mismatch.
    /// Whether that blocks is the calling operation's decision, not this type's.
    [Fact]
    public void An_absent_peer_is_not_comparable_rather_than_mismatched()
    {
        Assert.Equal(
            VersionSkewVerdict.NotComparable,
            VersionSkew.Between(Local, null).Verdict);
    }

    /// A binary built outside a repository reports "unknown" for both parts. It cannot be
    /// shown to match, and "cannot be shown to match" must never render as a match.
    [Fact]
    public void An_unknown_peer_build_is_not_comparable()
    {
        Assert.Equal(
            VersionSkewVerdict.NotComparable,
            VersionSkew.Between(Local, new BuildIdentity("unknown", "unknown")).Verdict);
    }

    /// The local side can be the unknown one just as easily, and it counts the same. An
    /// asymmetry here would mean a locally-unidentifiable binary happily driving a sequence
    /// across two hosts.
    [Fact]
    public void An_unknown_local_build_is_not_comparable_either()
    {
        Assert.Equal(
            VersionSkewVerdict.NotComparable,
            VersionSkew.Between(
                new BuildIdentity("unknown", "unknown"), new BuildIdentity("0.4.0", "abc123"))
                .Verdict);
    }

    /// A partially identified build is still unidentified: a known version with an unknown
    /// commit cannot establish that the two binaries are the same one.
    [Fact]
    public void A_known_version_with_an_unknown_commit_is_not_comparable()
    {
        Assert.Equal(
            VersionSkewVerdict.NotComparable,
            VersionSkew.Between(Local, new BuildIdentity("0.4.0", "unknown")).Verdict);
    }

    /// Every verdict is printed next to a decision to proceed or stop, so each one has to say
    /// which two builds it compared.
    [Fact]
    public void Skew_names_both_builds()
    {
        VersionSkew skew = VersionSkew.Between(Local, new BuildIdentity("0.3.0", "def456"));

        Assert.Contains("0.4.0", skew.Explanation, StringComparison.Ordinal);
        Assert.Contains("0.3.0", skew.Explanation, StringComparison.Ordinal);
    }
}
