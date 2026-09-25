using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Diagnostics;

public class LogFolderTests
{
    [Theory]
    [InlineData(@"C:\Program Files\Ripcord\ripcord.exe", @"C:\Program Files\Ripcord\logs")]
    [InlineData(@"D:\ripcord.exe", @"D:\logs")]
    public void The_logs_folder_sits_beside_the_binary(string binary, string folder) =>
        Assert.Equal(folder, LogFolder.Beside(binary));

    /// Named by the UTC day: a host in UTC+2 at 01:00 is still on the previous UTC day.
    [Fact]
    public void The_listener_log_is_named_by_its_utc_day() =>
        Assert.Equal(
            @"C:\Ripcord\logs\listener-2026-09-24.log",
            LogFolder.ListenerLog(
                @"C:\Ripcord\logs",
                new DateTimeOffset(2026, 9, 25, 1, 0, 0, TimeSpan.FromHours(2))));
}
