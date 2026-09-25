using System.Globalization;

namespace Ripcord.Domain.Diagnostics;

/// The one folder the listener service may write to. The service runs as a virtual account
/// with no write access anywhere else — least of all beside the binary, in Program Files —
/// so `service install` grants it this folder and nothing more.
public static class LogFolder
{
    public const string Name = "logs";

    public static string Beside(string binaryPath) =>
        WindowsPath.Join(WindowsPath.FolderOf(binaryPath), Name);

    /// One file per UTC day, appended to: a restart adds to the day's story rather than
    /// erasing the run somebody is asking about.
    public static string ListenerLog(string folder, DateTimeOffset at) =>
        WindowsPath.Join(
            folder,
            string.Create(
                CultureInfo.InvariantCulture, $"listener-{at.UtcDateTime:yyyy-MM-dd}.log"));
}
