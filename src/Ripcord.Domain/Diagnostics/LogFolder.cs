using System.Globalization;

namespace Ripcord.Domain.Diagnostics;

/// The `logs` folder and the files in it: one per origin and per UTC day, kept for a fixed
/// number of days.
///
/// It is also the one folder the listener service may write to. The service runs as a virtual
/// account with no write access anywhere else — least of all beside the binary, in Program
/// Files — so `service install` grants it this folder and nothing more.
public static class LogFolder
{
    public const string Name = "logs";

    /// Fixed, not configurable: a month covers the last monthly test failover, and a setting
    /// nobody reads is a setting somebody sets to zero.
    public const int RetentionDays = 30;

    private const string Extension = ".log";

    private const string PreviousSuffix = ".1";

    private const string DateFormat = "yyyy-MM-dd";

    public static string Beside(string binaryPath) =>
        WindowsPath.Join(WindowsPath.FolderOf(binaryPath), Name);

    /// The publishing service writes in a folder of its own below `logs`: the listener, which
    /// faces the network, can modify `logs` and must not be able to rewrite this one's story.
    public static string For(DiagnosticOrigin origin, string logsFolder) =>
        origin == DiagnosticOrigin.Publisher
            ? WindowsPath.Join(logsFolder, origin.Prefix())
            : logsFolder;

    public static string Prefix(this DiagnosticOrigin origin) => origin switch
    {
        DiagnosticOrigin.Listener => "listener",
        DiagnosticOrigin.Publisher => "publish",
        _ => "ripcord",
    };

    /// Named by the UTC day, so both hosts of the pair agree on which file a moment is in.
    public static string FileName(DiagnosticOrigin origin, DateTimeOffset at) =>
        origin.Prefix() + "-"
            + at.UtcDateTime.ToString(DateFormat, CultureInfo.InvariantCulture) + Extension;

    /// The one extra file a day may have, when it passes the size cap.
    public static string PreviousOf(string fileName) => fileName + PreviousSuffix;

    /// The files past retention, among the names found in the folder. Only names this code
    /// writes are ever returned: the folder may be one the operator chose, holding anything.
    /// Today and the days before it within retention are kept; a future date is never expired.
    public static IReadOnlyList<string> Expired(IEnumerable<string> fileNames, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(fileNames);

        DateOnly oldestKept =
            DateOnly.FromDateTime(now.UtcDateTime).AddDays(-(RetentionDays - 1));

        return [.. fileNames.Where(name => DayOf(name) is { } day && day < oldestKept)];
    }

    private static DateOnly? DayOf(string fileName)
    {
        string name = fileName.EndsWith(PreviousSuffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^PreviousSuffix.Length]
            : fileName;

        if (!name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        name = name[..^Extension.Length];

        foreach (DiagnosticOrigin origin in Enum.GetValues<DiagnosticOrigin>())
        {
            string prefix = origin.Prefix() + "-";

            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && DateOnly.TryParseExact(
                    name[prefix.Length..],
                    DateFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly day))
            {
                return day;
            }
        }

        return null;
    }
}
