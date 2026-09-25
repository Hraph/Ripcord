using System.Security;
using System.Text;
using Ripcord.Domain;
using Ripcord.Domain.Diagnostics;
using Ripcord.Ports.Diagnostics;

namespace Ripcord.Adapters.Diagnostics;

/// Reads the end of a log the listener may be appending to right now.
public sealed class FileDiagnosticLogReader : IDiagnosticLogReader
{
    /// Enough for a few hundred lines; a day's file can be megabytes of served connections.
    private const int WindowBytes = 64 * 1024;

    public LogReading? Tail(string path, int maxLines)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // Shared for writing and deleting: the service holds the file, and pruning may
            // remove it while it is read.
            using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            long start = Math.Max(0, stream.Length - WindowBytes);
            stream.Seek(start, SeekOrigin.Begin);

            using StreamReader reader = new(stream, Encoding.UTF8);

            List<string> lines = [.. reader.ReadToEnd()
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))];

            // Started mid-file, the first line is a fragment.
            if (start > 0 && lines.Count > 0)
            {
                lines.RemoveAt(0);
            }

            if (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return new LogReading(
                path, [.. lines.TakeLast(Math.Max(maxLines, 0)).Select(Printable.Of)], null);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or SecurityException)
        {
            return new LogReading(path, [], exception.Message);
        }
    }
}
