using Ripcord.Domain.Configuration;

namespace Ripcord.Domain.Diagnostics;

/// Which folder the diagnostic log is written to, and how large one day's file may grow.
///
/// Read from the raw document rather than from a validated configuration, and **tolerant of
/// everything it finds there**. A file whose job is to explain why a command failed must not
/// become a reason a command refuses to run — least of all on the host where the only way to
/// see anything is that file. So a figure out of range is replaced by the default and a blank
/// path falls back to the `logs` folder; nothing here produces an error.
///
/// That is the opposite of every other section, and deliberately so: the rest of `ripcord.yaml`
/// describes the pair, where a wrong value moves production to the wrong place. This describes
/// a log.
public sealed record DiagnosticDestination(bool Enabled, string Folder, long MaxBytes)
{
    /// Big enough to hold a busy day on a host nobody touches, small enough to be pasted
    /// somewhere or sent as an attachment.
    public const int DefaultMaxSizeMb = 5;

    /// A gigabyte of debug log is a full volume on a host whose volume matters. Past this the
    /// figure is not believed.
    public const int MaxSizeMbCeiling = 1_024;

    private const string OldFileExtension = ".log";

    public static DiagnosticDestination Default(string folder) =>
        new(true, folder, (long)DefaultMaxSizeMb * 1024 * 1024);

    public static DiagnosticDestination From(DiagnosticsDocument? document, string defaultFolder)
    {
        if (document is null)
        {
            return Default(defaultFolder);
        }

        int megabytes = document.MaxSizeMb is { } given and > 0 and <= MaxSizeMbCeiling
            ? given
            : DefaultMaxSizeMb;

        // Nullable, unlike the listener's and the dashboard's: those two are off until the file
        // switches them on, and this one is on until the file switches it off. A plain `bool`
        // would read a block that only sets `path` as a block that switches logging off.
        return new DiagnosticDestination(
            document.Enabled ?? true,
            FolderFrom(document.Path, defaultFolder),
            (long)megabytes * 1024 * 1024);
    }

    /// A service writes only to the folder `service install` granted it, whatever the
    /// configuration asks: a folder it cannot write is a service with no log at all.
    public DiagnosticDestination Applied(DiagnosticOrigin origin, DiagnosticDestination asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        return origin == DiagnosticOrigin.Command ? asked : this;
    }

    /// Rotation is decided before the write, not after: a file allowed past its limit and
    /// truncated afterwards is a file that was over the limit on the one occasion the host ran
    /// out of room.
    public bool MustRotate(long existingBytes, long incomingBytes) =>
        existingBytes > 0 && existingBytes + incomingBytes > this.MaxBytes;

    /// `path` used to name a file. A configuration written then still works: a value ending in
    /// `.log` is read as the folder holding it.
    private static string FolderFrom(string? path, string defaultFolder)
    {
        if (path is null || path.Trim().Length == 0)
        {
            return defaultFolder;
        }

        string given = path.Trim();

        if (!given.EndsWith(OldFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return given;
        }

        return WindowsPath.FolderOf(given) is { Length: > 0 } folder ? folder : defaultFolder;
    }
}
