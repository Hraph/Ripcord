namespace Ripcord.Cli.Rendering;

/// One console line redrawn in place; padded, not escape-cleared, as the console may lack VT.
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
