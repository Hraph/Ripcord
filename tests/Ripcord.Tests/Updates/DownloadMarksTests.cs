using Ripcord.Domain.Updates;

namespace Ripcord.Tests.Updates;

public sealed class DownloadMarksTests
{
    private const long Megabyte = 1024 * 1024;

    [Fact]
    public void A_sized_download_is_marked_every_percent()
    {
        DownloadMarks marks = new();
        List<string> seen = [];

        for (long received = 0; received <= 1000; received += 5)
        {
            if (marks.Next(new DownloadedBytes(received, 1000)) is { } mark)
            {
                seen.Add(mark);
            }
        }

        Assert.Equal(Enumerable.Range(1, 100).Select(percent => $"{percent}%"), seen);
    }

    [Fact]
    public void A_mark_already_given_is_not_given_again()
    {
        DownloadMarks marks = new();

        Assert.Equal("50%", marks.Next(new DownloadedBytes(50, 100)));
        Assert.Null(marks.Next(new DownloadedBytes(50, 100)));
    }

    /// A server that sends more than it announced must not print 150%.
    [Fact]
    public void More_than_announced_stops_at_a_hundred()
    {
        Assert.Equal("100%", new DownloadMarks().Next(new DownloadedBytes(300, 100)));
    }

    [Fact]
    public void An_unsized_download_is_marked_every_megabyte()
    {
        DownloadMarks marks = new();

        Assert.Null(marks.Next(new DownloadedBytes(Megabyte - 1, null)));
        Assert.Equal("1 MB", marks.Next(new DownloadedBytes(Megabyte, null)));
        Assert.Equal("3 MB", marks.Next(new DownloadedBytes(3 * Megabyte + 5, null)));
    }

    [Fact]
    public void A_size_of_zero_is_treated_as_no_size()
    {
        Assert.Equal("2 MB", new DownloadMarks().Next(new DownloadedBytes(2 * Megabyte, 0)));
    }
}
