using Ripcord.Domain.Updates;

namespace Ripcord.Tests.Updates;

/// What a host says about a release it heard of, possibly a long time ago. The file is written
/// by whatever last looked; this is the decision about whether what it says is still worth
/// putting in front of somebody.
///
/// It is read during an incident, above a verdict that matters more, so the bar is high: it
/// appears when it is true and useful, and says nothing at all the rest of the time.
public sealed class UpdateNoticeTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_newer_release_than_the_one_running_is_worth_saying()
    {
        string? line = UpdateNotices.For(Seen("0.2.1", Now.AddHours(-3)), "0.2.0", Now);

        Assert.NotNull(line);
        Assert.Contains("0.2.1", line, StringComparison.Ordinal);
        Assert.Contains("ripcord update", line, StringComparison.Ordinal);
    }

    /// The file still names the release this host just installed. Saying it again would send
    /// somebody to run an update that has already happened.
    [Fact]
    public void The_release_this_host_is_already_running_is_not_news()
    {
        Assert.Null(UpdateNotices.For(Seen("0.2.1", Now.AddHours(-3)), "0.2.1", Now));
    }

    [Fact]
    public void A_release_older_than_the_one_running_is_not_news_either()
    {
        Assert.Null(UpdateNotices.For(Seen("0.2.0", Now.AddHours(-3)), "0.2.1", Now));
    }

    [Fact]
    public void A_host_that_has_never_looked_says_nothing()
    {
        Assert.Null(UpdateNotices.For(null, "0.2.0", Now));
    }

    /// Neither half may be guessed at. A version that cannot be read is not quietly treated as
    /// newer, and it is not treated as older either — it is not mentioned.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void A_version_that_cannot_be_read_is_not_mentioned(string version)
    {
        Assert.Null(UpdateNotices.For(Seen(version, Now.AddHours(-3)), "0.2.0", Now));
        Assert.Null(UpdateNotices.For(Seen("0.2.1", Now.AddHours(-3)), version, Now));
    }

    /// How old the answer is, because a release heard of this morning and one heard of in
    /// April are different claims and the line has room to say which.
    [Fact]
    public void The_line_says_when_the_release_was_heard_of()
    {
        string? line = UpdateNotices.For(Seen("0.2.1", Now.AddDays(-40)), "0.2.0", Now);

        Assert.NotNull(line);
        Assert.Contains("2026-08-05", line, StringComparison.Ordinal);
    }

    /// A file written by a clock that was wrong, or copied from another host. It is reported
    /// as the timestamp it carries rather than as a negative age.
    [Fact]
    public void A_notice_from_the_future_is_still_only_a_timestamp()
    {
        string? line = UpdateNotices.For(Seen("0.2.1", Now.AddDays(2)), "0.2.0", Now);

        Assert.NotNull(line);
        Assert.Contains("2026-09-16", line, StringComparison.Ordinal);
    }

    /// The commit suffix a running build carries is not a version difference.
    [Fact]
    public void The_running_build_is_compared_without_its_commit()
    {
        Assert.Null(UpdateNotices.For(Seen("0.2.1", Now), "0.2.1+1a25acf92f81", Now));
    }

    private static UpdateNotice Seen(string version, DateTimeOffset at) => new(version, at);
}
