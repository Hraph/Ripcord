using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Deployment;

/// What a failed listener start leaves in the Application event log: the only trace, so it
/// has to name the file, the reason and the command that repairs it.
public class ServiceStartupTests
{
    private const string LogPath = @"C:\Program Files\Ripcord\logs\listener-2026-09-25.log";

    [Fact]
    public void An_unwritable_log_names_the_file_the_reason_the_account_and_the_fix()
    {
        string text = ServiceStartup.LogUnavailable(RipcordService.Listener, LogPath, "Access is denied.");

        Assert.Contains(LogPath, text, StringComparison.Ordinal);
        Assert.Contains("Access is denied.", text, StringComparison.Ordinal);
        Assert.Contains(RipcordService.Listener.Account, text, StringComparison.Ordinal);
        Assert.Contains(@"C:\Program Files\Ripcord\logs", text, StringComparison.Ordinal);
        Assert.Contains("ripcord service install", text, StringComparison.Ordinal);
        Assert.Contains("'ripcord service'", text, StringComparison.Ordinal);
    }

    /// Read with Get-WinEvent on the same 1024x768 console as everything else.
    [Fact]
    public void Every_line_of_it_fits_the_console() =>
        Assert.All(
            ServiceStartup.LogUnavailable(RipcordService.Listener, LogPath, "Access is denied.").Split(Environment.NewLine),
            line => Assert.True(line.Length <= 75, line));

    [Fact]
    public void The_banner_says_which_version_and_which_configuration()
    {
        string banner = string.Join(
            "\n",
            ServiceStartup.Banner(RipcordService.Listener, "0.4.0+32aac02", @"C:\Program Files\Ripcord\ripcord.yaml")
                .Render(new DateTimeOffset(2026, 9, 25, 8, 30, 0, TimeSpan.FromHours(2))));

        Assert.Contains("2026-09-25 06:30:00.000Z", banner, StringComparison.Ordinal);
        Assert.Contains("0.4.0+32aac02", banner, StringComparison.Ordinal);
        Assert.Contains(@"C:\Program Files\Ripcord\ripcord.yaml", banner, StringComparison.Ordinal);
    }
}
