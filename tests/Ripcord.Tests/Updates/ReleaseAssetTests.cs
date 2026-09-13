using Ripcord.Domain.Updates;

namespace Ripcord.Tests.Updates;

/// What a release has to carry. The binary alone is not a release this tool will install: the
/// signature is not an extra, it is the half that makes the other half installable.
public sealed class ReleaseAssetTests
{
    [Fact]
    public void A_release_is_the_binary_and_the_signature_beside_it()
    {
        Assert.Equal(["ripcord.exe", "ripcord.exe.sig"], ReleaseAssets.Required);
    }

    [Fact]
    public void A_complete_release_is_missing_nothing()
    {
        Assert.Empty(ReleaseAssets.MissingFrom(["ripcord.exe", "ripcord.exe.sig"]));
    }

    /// The case that matters. A release published without its signature is refused by name,
    /// because it cannot be verified — never reported as a download that failed.
    [Fact]
    public void A_release_with_no_signature_names_the_signature()
    {
        Assert.Equal(["ripcord.exe.sig"], ReleaseAssets.MissingFrom(["ripcord.exe"]));
    }

    [Fact]
    public void A_release_with_nothing_in_it_names_both()
    {
        Assert.Equal(["ripcord.exe", "ripcord.exe.sig"], ReleaseAssets.MissingFrom([]));
    }

    [Fact]
    public void A_release_that_could_not_be_read_names_both_rather_than_none()
    {
        Assert.Equal(["ripcord.exe", "ripcord.exe.sig"], ReleaseAssets.MissingFrom(null));
    }

    /// The checksum rides along beside them and is nothing to do with whether the release can
    /// be installed; it must not make a release look incomplete.
    [Fact]
    public void What_else_a_release_carries_is_not_this_function_s_business()
    {
        Assert.Empty(ReleaseAssets.MissingFrom(
            ["ripcord.exe.sha256", "ripcord.exe", "ripcord.exe.sig"]));
    }

    [Fact]
    public void The_names_are_matched_whatever_case_they_are_written_in()
    {
        Assert.Empty(ReleaseAssets.MissingFrom(["RIPCORD.EXE", "Ripcord.exe.Sig"]));
    }
}
