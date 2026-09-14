using System.Text;
using Ripcord.Domain.Diagnostics;
using Ripcord.Ports.Diagnostics;
using Ripcord.Ports;

namespace Ripcord.Adapters.Diagnostics;

/// The diagnostic log as a plain text file, appended to and rotated once.
///
/// Deliberately not the audit trail's neighbour. That file is JSON Lines, never rewritten, and
/// its value is that nothing removes a line; this one is read with `type` on a console and
/// **is** trimmed when it gets large. Keeping them in separate files — and separate projects —
/// is what stops rotation ever being pointed at the trail.
///
/// Every failure here is swallowed. A log that throws takes down the command it exists to
/// explain, and on a host with no console that is the difference between a bad morning and an
/// unexplained one.
public sealed class FileDiagnosticLog(IClock clock, DiagnosticDestination destination)
    : IDiagnosticLog
{
    private readonly Lock gate = new();

    private DiagnosticDestination current = destination;

    public void SendTo(DiagnosticDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        lock (this.gate)
        {
            this.current = destination;
        }
    }

    public void Write(DiagnosticEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        DiagnosticDestination writing;

        lock (this.gate)
        {
            writing = this.current;
        }

        if (!writing.Enabled)
        {
            return;
        }

        // The newline is written explicitly rather than by the framework: the listener writes
        // this file as a service and an operator reads it over a share from the other host, so
        // one format whatever wrote it.
        byte[] bytes = Encoding.UTF8.GetBytes(
            string.Concat(entry.Render(clock.UtcNow).Select(line => line + "\r\n")));

        try
        {
            // Within this process only. The listener runs as a separate service and appends
            // to the same file, which is why each entry is built in full and written in one
            // call rather than line by line: two processes cannot be locked against each
            // other here, but one write each is the smallest thing they can interleave.
            lock (this.gate)
            {
                Rotate(writing, bytes.Length);

                using FileStream stream = new(
                    writing.Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

                stream.Write(bytes);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
        {
            // A read-only directory, a path that is not a path, another process holding the
            // file. None of them is a reason for the command to stop.
        }
    }

    /// One generation kept, replaced rather than numbered. A series of `.1 .2 .3` files is what
    /// fills the volume the log was written to explain — and on these hosts that volume is the
    /// one the VMs live on.
    private static void Rotate(DiagnosticDestination writing, int incomingBytes)
    {
        FileInfo current = new(writing.Path);

        if (!current.Exists)
        {
            // The operator may have pointed the log at a directory that does not exist yet —
            // `D:\Ripcord` on a host where only the binary's own folder was created.
            if (Path.GetDirectoryName(current.FullName) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            return;
        }

        if (writing.MustRotate(current.Length, incomingBytes))
        {
            File.Move(writing.Path, writing.PreviousPath, overwrite: true);
        }
    }
}
