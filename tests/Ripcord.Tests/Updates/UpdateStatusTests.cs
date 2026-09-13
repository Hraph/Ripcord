using Ripcord.Domain.Updates;

namespace Ripcord.Tests.Updates;

/// Comparing two version strings, and refusing to compare the ones that cannot be read. The
/// whole feature is allowed to say "a newer version exists" and nothing else, so this is the
/// only decision in it.
public class UpdateStatusTests
{
    [Fact]
    public void A_newer_release_is_an_update()
    {
        UpdateStatus status = UpdateStatus.Between("0.4.0", "0.5.0");

        Assert.Equal(UpdateVerdict.UpdateAvailable, status.Verdict);
        Assert.Contains("0.5.0", status.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_version_is_up_to_date() =>
        Assert.Equal(UpdateVerdict.UpToDate, UpdateStatus.Between("0.4.0", "0.4.0").Verdict);

    /// A build newer than the latest release is a build made from the branch. It is not an
    /// update and it is not an error; saying "up to date" would be a lie about which of the
    /// two is which, so it says what it sees.
    [Fact]
    public void A_build_ahead_of_the_latest_release_is_not_an_update()
    {
        UpdateStatus status = UpdateStatus.Between("0.6.0", "0.5.0");

        Assert.Equal(UpdateVerdict.UpToDate, status.Verdict);
        Assert.Contains("ahead", status.Explanation, StringComparison.Ordinal);
    }

    /// GitHub tags are written `v0.5.0` as often as `0.5.0`, and both name the same release.
    [Fact]
    public void A_tag_with_a_leading_v_names_the_same_version() =>
        Assert.Equal(UpdateVerdict.UpToDate, UpdateStatus.Between("0.4.0", "v0.4.0").Verdict);

    /// The commit hash this binary stamps into its informational version is not part of the
    /// comparison.
    [Fact]
    public void The_commit_part_of_a_version_is_ignored() =>
        Assert.Equal(
            UpdateVerdict.UpToDate, UpdateStatus.Between("0.4.0+abc123def456", "0.4.0").Verdict);

    /// "Cannot be compared" is its own answer and never folded into "up to date": a confident
    /// wrong answer is the failure mode this tool exists to avoid.
    [Theory]
    [InlineData("0.4.0", null)]
    [InlineData("0.4.0", "")]
    [InlineData("0.4.0", "nightly")]
    [InlineData("unknown", "0.5.0")]
    public void A_version_that_cannot_be_read_is_not_comparable(string running, string? latest) =>
        Assert.Equal(
            UpdateVerdict.NotComparable, UpdateStatus.Between(running, latest).Verdict);
}
