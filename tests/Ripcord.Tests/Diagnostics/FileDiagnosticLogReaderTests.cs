using System.Text;
using Ripcord.Adapters.Diagnostics;
using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Diagnostics;

/// Reading the end of the listener's log, as `ripcord service` does.
public class FileDiagnosticLogReaderTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), Path.GetRandomFileName());

    private readonly FileDiagnosticLogReader reader = new();

    public FileDiagnosticLogReaderTests() => Directory.CreateDirectory(this.directory);

    private string File(string name) => Path.Combine(this.directory, name);

    [Fact]
    public void Returns_the_last_n_lines()
    {
        string path = this.File("listener.log");
        System.IO.File.WriteAllText(path, "one\r\ntwo\r\nthree\r\n");

        LogReading? reading = this.reader.Tail(path, 2);

        Assert.Equal(new LogReading(path, ["two", "three"], null), reading);
    }

    [Fact]
    public void Absent_file_is_null() =>
        Assert.Null(this.reader.Tail(this.File("absent.log"), 20));

    [Fact]
    public void A_file_held_open_for_append_is_still_read()
    {
        string path = this.File("held.log");

        using FileStream writer = new(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write(Encoding.UTF8.GetBytes("serving\r\n"));
        writer.Flush();

        Assert.Equal(["serving"], this.reader.Tail(path, 20)?.Lines);
    }

    [Fact]
    public void A_large_file_is_read_from_its_end_without_a_fragment()
    {
        string path = this.File("large.log");
        System.IO.File.WriteAllLines(
            path, Enumerable.Range(0, 20_000).Select(index => $"line {index:D6} padding"));

        LogReading? reading = this.reader.Tail(path, 500);

        Assert.Equal(500, reading?.Lines.Count);
        Assert.Equal("line 019999 padding", reading?.Lines[^1]);
        Assert.All(reading!.Lines, line => Assert.StartsWith("line ", line, StringComparison.Ordinal));
    }

    [Fact]
    public void Control_characters_are_scrubbed()
    {
        string path = this.File("escape.log");
        System.IO.File.WriteAllText(path, "a\u001b[2Jb\n");

        Assert.Equal(["a?[2Jb"], this.reader.Tail(path, 20)?.Lines);
    }

    public void Dispose()
    {
        Directory.Delete(this.directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
