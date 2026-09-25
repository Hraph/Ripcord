using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Diagnostics;

public class LogFolderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(@"C:\Program Files\Ripcord\ripcord.exe", @"C:\Program Files\Ripcord\logs")]
    [InlineData(@"D:\ripcord.exe", @"D:\logs")]
    public void The_logs_folder_sits_beside_the_binary(string binary, string folder) =>
        Assert.Equal(folder, LogFolder.Beside(binary));

    /// Named by the UTC day: a host in UTC+2 at 01:00 is still on the previous UTC day.
    [Theory]
    [InlineData(DiagnosticOrigin.Command, "ripcord-2026-09-24.log")]
    [InlineData(DiagnosticOrigin.Listener, "listener-2026-09-24.log")]
    public void The_file_name_is_the_stream_and_the_utc_date(
        DiagnosticOrigin origin, string expected) =>
        Assert.Equal(
            expected,
            LogFolder.FileName(
                origin, new DateTimeOffset(2026, 9, 25, 1, 0, 0, TimeSpan.FromHours(2))));

    [Fact]
    public void The_previous_file_of_a_day_is_that_day_with_dot_one() =>
        Assert.Equal(
            "ripcord-2026-09-25.log.1", LogFolder.PreviousOf("ripcord-2026-09-25.log"));

    /// Today and the 29 days before it are kept: thirty files an origin, no more.
    [Theory]
    [InlineData("ripcord-2026-08-26.log", true)]
    [InlineData("listener-2026-08-26.log.1", true)]
    [InlineData("LISTENER-2025-01-01.LOG", true)]
    [InlineData("ripcord-2026-08-27.log", false)]
    [InlineData("listener-2026-09-25.log", false)]
    [InlineData("ripcord-2026-09-26.log", false)]
    public void A_file_expires_after_the_retention(string name, bool expired) =>
        Assert.Equal(expired, LogFolder.Expired([name], Now).Count == 1);

    /// The folder may be one the operator chose, and it may hold anything.
    [Theory]
    [InlineData("ripcord.log")]
    [InlineData("ripcord.log.1")]
    [InlineData("listener.log")]
    [InlineData("notes.txt")]
    [InlineData("ripcord-2026-13-45.log")]
    [InlineData("other-2020-01-01.log")]
    [InlineData("ripcord-2020-01-01.log.2")]
    [InlineData("ripcord-2020-01-01.txt")]
    public void Pruning_never_names_a_file_it_did_not_write(string name) =>
        Assert.Empty(LogFolder.Expired([name], Now));
}
