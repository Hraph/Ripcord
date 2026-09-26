using System.Globalization;

namespace Ripcord.Domain.Updates;

/// How much of a download has arrived. `Total` is what the server announced, when it did.
public readonly record struct DownloadedBytes(long Received, long? Total);

/// Decides when a download has moved far enough to be worth a mark on the console: every tenth
/// of the announced size, or every twenty megabytes when no size was announced — few enough that
/// a release at the size cap still fits one console line.
public sealed class DownloadMarks
{
    private const long Megabyte = 1024 * 1024;

    private const long UnsizedStep = 20 * Megabyte;

    private long reached;

    /// The next mark, or null while the download is still short of it.
    public string? Next(DownloadedBytes bytes)
    {
        if (bytes.Total is > 0 and long total)
        {
            long tenths = Math.Min(bytes.Received * 10 / total, 10);

            if (tenths <= this.reached)
            {
                return null;
            }

            this.reached = tenths;
            return string.Create(CultureInfo.InvariantCulture, $"{tenths * 10}%");
        }

        long steps = bytes.Received / UnsizedStep;

        if (steps <= this.reached)
        {
            return null;
        }

        this.reached = steps;
        return string.Create(CultureInfo.InvariantCulture, $"{steps * UnsizedStep / Megabyte} MB");
    }
}
