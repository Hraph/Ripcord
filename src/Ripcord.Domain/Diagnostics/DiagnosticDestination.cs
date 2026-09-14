using Ripcord.Domain.Configuration;

namespace Ripcord.Domain.Diagnostics;

/// Where the diagnostic log is written, and how large it may grow.
///
/// Read from the raw document rather than from a validated configuration, and **tolerant of
/// everything it finds there**. A file whose job is to explain why a command failed must not
/// become a reason a command refuses to run — least of all on the host where the only way to
/// see anything is that file. So a figure out of range is replaced by the default and a blank
/// path falls back beside the binary; nothing here produces an error.
///
/// That is the opposite of every other section, and deliberately so: the rest of `ripcord.yaml`
/// describes the pair, where a wrong value moves production to the wrong place. This describes
/// a log.
public sealed record DiagnosticDestination(bool Enabled, string Path, long MaxBytes)
{
    /// Beside the binary, like the audit trail and the alert state: one directory holds
    /// everything a host writes about itself, and it is the directory the operator is already
    /// standing in.
    public const string DefaultFileName = "ripcord.log";

    /// Big enough to hold every run since the last update on a host nobody touches, small
    /// enough to be pasted somewhere or sent as an attachment.
    public const int DefaultMaxSizeMb = 5;

    /// A gigabyte of debug log is a full volume on a host whose volume matters. Past this the
    /// figure is not believed.
    public const int MaxSizeMbCeiling = 1_024;

    /// The previous file, kept whole. Two files and no more: a log that rotates into an
    /// unbounded series is the thing that fills the volume it was meant to explain.
    public string PreviousPath => this.Path + ".1";

    public static DiagnosticDestination Default(string path) =>
        new(true, path, (long)DefaultMaxSizeMb * 1024 * 1024);

    public static DiagnosticDestination From(DiagnosticsDocument? document, string defaultPath)
    {
        if (document is null)
        {
            return Default(defaultPath);
        }

        int megabytes = document.MaxSizeMb is { } given and > 0 and <= MaxSizeMbCeiling
            ? given
            : DefaultMaxSizeMb;

        // Nullable, unlike the listener's and the dashboard's: those two are off until the file
        // switches them on, and this one is on until the file switches it off. A plain `bool`
        // would read a block that only sets `path` as a block that switches logging off.
        return new DiagnosticDestination(
            document.Enabled ?? true,
            document.Path is { } path && path.Trim().Length > 0 ? path.Trim() : defaultPath,
            (long)megabytes * 1024 * 1024);
    }

    /// Rotation is decided before the write, not after: a file allowed past its limit and
    /// truncated afterwards is a file that was over the limit on the one occasion the host ran
    /// out of room.
    public bool MustRotate(long existingBytes, long incomingBytes) =>
        existingBytes > 0 && existingBytes + incomingBytes > this.MaxBytes;
}
