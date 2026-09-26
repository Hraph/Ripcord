using System.Globalization;

namespace Ripcord.Domain.Updates;

/// How much of a download has arrived. `Total` is what the server announced, when it did.
public readonly record struct DownloadedBytes(long Received, long? Total);

/// Decides when a download has moved far enough to redraw: every percent of the announced
/// size, or every megabyte when no size was announced — not on every chunk of a few kilobytes.
public sealed class DownloadMarks
{
    private const long Megabyte = 1024 * 1024;

    private long reached;

    /// The next mark, or null while the download is still short of it.
    public string? Next(DownloadedBytes bytes)
    {
        if (bytes.Total is > 0 and long total)
        {
            long percent = Math.Min(bytes.Received * 100 / total, 100);

            if (percent <= this.reached)
            {
                return null;
            }

            this.reached = percent;
            return string.Create(CultureInfo.InvariantCulture, $"{percent}%");
        }

        long megabytes = bytes.Received / Megabyte;

        if (megabytes <= this.reached)
        {
            return null;
        }

        this.reached = megabytes;
        return string.Create(CultureInfo.InvariantCulture, $"{megabytes} MB");
    }
}
