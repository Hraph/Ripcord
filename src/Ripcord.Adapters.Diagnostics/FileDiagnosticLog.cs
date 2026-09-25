using System.Security;
using System.Text;
using Ripcord.Domain.Diagnostics;
using Ripcord.Ports.Diagnostics;
using Ripcord.Ports;

namespace Ripcord.Adapters.Diagnostics;

/// The diagnostic log as plain text files, one per origin and per UTC day, appended to.
///
/// Deliberately not the audit trail's neighbour. That file is JSON Lines, never rewritten, and
/// its value is that nothing removes a line; these are read with `type` on a console and
/// **are** trimmed and pruned. Keeping them in separate files — and separate projects — is
/// what stops rotation ever being pointed at the trail.
///
/// No handle is held between writes: each one works out today's file from the clock, so the
/// service running for weeks moves to a new file at midnight UTC like a command would.
///
/// Every failure here is swallowed. A log that throws takes down the command it exists to
/// explain, and on a host with no console that is the difference between a bad morning and an
/// unexplained one.
public sealed class FileDiagnosticLog(
    IClock clock, DiagnosticOrigin origin, DiagnosticDestination destination) : IDiagnosticLog
{
    private readonly Lock gate = new();

    private DiagnosticDestination current = destination;

    /// The file last written to; a different one means a new folder or a new day.
    private string? lastFile;

    /// Today's file, where the next line will go.
    public string CurrentFile
    {
        get
        {
            lock (this.gate)
            {
                return this.FileAt(this.current, clock.UtcNow);
            }
        }
    }

    public void SendTo(DiagnosticDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        lock (this.gate)
        {
            this.current = this.current.Applied(origin, destination);
        }
    }

    public void Write(DiagnosticEntry entry) => _ = this.TryWrite(entry);

    /// Null once written, or why it could not be. Only the listener's first line looks: a
    /// service that cannot write its log has to say so somewhere else.
    public string? TryWrite(DiagnosticEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        DateTimeOffset now = clock.UtcNow;

        // The newline is written explicitly rather than by the framework: the listener writes
        // these files as a service and an operator reads them over a share from the other host,
        // so one format whatever wrote it.
        byte[] bytes = Encoding.UTF8.GetBytes(
            string.Concat(entry.Render(now).Select(line => line + "\r\n")));

        try
        {
            // Within this process only. A command and the listener may append to the same
            // folder, which is why each entry is built in full and written in one call rather
            // than line by line: two processes cannot be locked against each other here, but
            // one write each is the smallest thing they can interleave.
            lock (this.gate)
            {
                if (!this.current.Enabled)
                {
                    return null;
                }

                string file = this.FileAt(this.current, now);

                // The operator may have pointed the log at a folder that does not exist yet —
                // `D:\Ripcord` on a host where only the binary's own folder exists — or somebody
                // emptied it while the service was running.
                Directory.CreateDirectory(this.current.Folder);

                if (file != this.lastFile)
                {
                    Prune(this.current.Folder, now);
                    this.lastFile = file;
                }

                Rotate(this.current, file, bytes.Length);

                using FileStream output = new(
                    file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

                output.Write(bytes);
            }

            return null;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            // A read-only folder, a path that is not a path, another process holding the
            // file. None of them is a reason for the command to stop.
            return exception.Message;
        }
    }

    private string FileAt(DiagnosticDestination writing, DateTimeOffset at) =>
        Path.Combine(writing.Folder, LogFolder.FileName(origin, at));

    /// Once per process and once per day, never more: a listing per line would be a listing
    /// per served connection.
    private static void Prune(string folder, DateTimeOffset now)
    {
        IReadOnlyList<string> expired;

        try
        {
            expired = LogFolder.Expired(
                Directory.EnumerateFiles(folder).Select(Path.GetFileName).OfType<string>(), now);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return;
        }

        foreach (string name in expired)
        {
            try
            {
                File.Delete(Path.Combine(folder, name));
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                // Held by the other process, or not ours to delete. Next day it is tried again.
            }
        }
    }

    /// One extra file a day, replaced rather than numbered: a series of `.1 .2 .3` files is
    /// what fills the volume the log was written to explain — and on these hosts that volume
    /// is the one the VMs live on.
    private static void Rotate(DiagnosticDestination writing, string file, int incomingBytes)
    {
        try
        {
            FileInfo existing = new(file);

            if (existing.Exists && writing.MustRotate(existing.Length, incomingBytes))
            {
                File.Move(file, LogFolder.PreviousOf(file), overwrite: true);
            }
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            // The other process is rotating the same file. The line is still worth writing.
        }
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or SecurityException;
}
