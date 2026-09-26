using Ripcord.Domain.Updates;

namespace Ripcord.Tests.Updates;

public sealed class DownloadMarksTests
{
    private const long Megabyte = 1024 * 1024;

    [Fact]
    public void A_sized_download_is_marked_every_tenth()
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

        Assert.Equal(["10%", "20%", "30%", "40%", "50%", "60%", "70%", "80%", "90%", "100%"], seen);
    }

    [Fact]
    public void A_mark_already_given_is_not_given_again()
    {
        DownloadMarks marks = new();

        Assert.Equal("50%", marks.Next(new DownloadedBytes(50, 100)));
        Assert.Null(marks.Next(new DownloadedBytes(55, 100)));
    }

    /// A server that sends more than it announced must not print 150%.
    [Fact]
    public void More_than_announced_stops_at_a_hundred()
    {
        Assert.Equal("100%", new DownloadMarks().Next(new DownloadedBytes(300, 100)));
    }

    [Fact]
    public void An_unsized_download_is_marked_every_twenty_megabytes()
    {
        DownloadMarks marks = new();

        Assert.Null(marks.Next(new DownloadedBytes(19 * Megabyte, null)));
        Assert.Equal("20 MB", marks.Next(new DownloadedBytes(20 * Megabyte, null)));
        Assert.Equal("40 MB", marks.Next(new DownloadedBytes(55 * Megabyte, null)));
    }

    [Fact]
    public void A_size_of_zero_is_treated_as_no_size()
    {
        Assert.Equal("20 MB", new DownloadMarks().Next(new DownloadedBytes(20 * Megabyte, 0)));
    }

    /// A release at the size cap, announced with no size, still fits one console line.
    [Fact]
    public void Unsized_marks_up_to_the_size_cap_fit_the_console()
    {
        DownloadMarks marks = new();
        string line = "       ";

        for (long received = 0; received <= 160 * Megabyte; received += Megabyte)
        {
            if (marks.Next(new DownloadedBytes(received, null)) is { } mark)
            {
                line += " " + mark;
            }
        }

        Assert.InRange(line.Length, 1, 75);
    }
}
