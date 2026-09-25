using Ripcord.Domain.Diagnostics;

namespace Ripcord.Domain.Deployment;

/// What the listener service says about its own start, where an operator will read it.
///
/// Written here rather than in the host so its wording is tested: the host cannot run off
/// Windows, and these lines are the whole of what a failed start leaves behind.
public static class ListenerStartup
{
    /// For the Application event log, the one place left when the log file cannot be opened.
    /// Paths go on lines of their own: a Program Files path does not share 75 columns.
    public static string LogUnavailable(string logPath, string reason) =>
        string.Join(
            Environment.NewLine,
            "The Ripcord listener stopped: it cannot write its log",
            $"  {logPath}",
            $"  {reason}",
            "Run 'ripcord service install' as an administrator: it grants",
            $"{DeploymentPlan.ServiceAccount} access to the folder",
            $"  {WindowsPath.FolderOf(logPath)}",
            "Then run 'ripcord service' to check.");

    /// First lines of every start, so a log appended to all day says where each run begins.
    public static DiagnosticEntry Banner(string version, string configurationPath) =>
        new(
            BannerOperation,
            $"{BannerStart}{version}{BannerEnd}",
            [$"configuration {configurationPath}"]);

    /// Read back by `ripcord service`: what follows the last banner is the last run.
    public static bool IsBanner(string operation, string message) =>
        operation == BannerOperation
        && message.StartsWith(BannerStart, StringComparison.Ordinal)
        && message.EndsWith(BannerEnd, StringComparison.Ordinal);

    private const string BannerOperation = "service";

    private const string BannerStart = "==== ripcord ";

    private const string BannerEnd = " listener starting";
}
