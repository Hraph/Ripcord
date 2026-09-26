namespace Ripcord.Cli.Rendering;

/// One console line, redrawn in place and erased when done. Padded with spaces rather than
/// cleared with an escape code, because the console may have no virtual terminal support.
/// Draws nothing when the output is not a live console: a file would keep every redraw.
public sealed class ProgressLine(TextWriter output, bool live)
{
    private int shown;

    public void Show(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!live)
        {
            return;
        }

        output.Write("\r" + text.PadRight(this.shown));
        this.shown = text.Length;
    }

    public void Clear()
    {
        if (!live || this.shown == 0)
        {
            return;
        }

        output.Write("\r" + new string(' ', this.shown) + "\r");
        this.shown = 0;
    }
}
