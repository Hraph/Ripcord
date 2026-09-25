using System.Globalization;

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
    public static string Banner(string version, string configurationPath, DateTimeOffset at) =>
        string.Join(
            Environment.NewLine,
            string.Create(
                CultureInfo.InvariantCulture,
                $"==== {at.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z ripcord {version} listener starting"),
            $"     configuration {configurationPath}");
}
