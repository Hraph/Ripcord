using Ripcord.Cli.Rendering;

namespace Ripcord.Tests.Rendering;

public sealed class ProgressLineTests
{
    /// A shorter text drawn over a longer one leaves nothing of it behind.
    [Fact]
    public void A_shorter_text_covers_the_longer_one_before_it()
    {
        StringWriter output = new();
        ProgressLine line = new(output, live: true);

        line.Show("long text");
        line.Show("short");

        Assert.Equal("\rlong text\rshort    ", output.ToString());
    }

    [Fact]
    public void Clearing_leaves_the_cursor_at_the_start_of_an_empty_line()
    {
        StringWriter output = new();
        ProgressLine line = new(output, live: true);

        line.Show("abc");
        line.Clear();

        Assert.Equal("\rabc\r   \r", output.ToString());
    }

    [Fact]
    public void Nothing_is_drawn_when_the_output_is_not_a_console()
    {
        StringWriter output = new();
        ProgressLine line = new(output, live: false);

        line.Show("abc");
        line.Clear();

        Assert.Empty(output.ToString());
    }
}
