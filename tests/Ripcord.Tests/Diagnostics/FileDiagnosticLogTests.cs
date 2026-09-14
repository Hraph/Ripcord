using Ripcord.Adapters.Diagnostics;
using Ripcord.Domain.Diagnostics;
using Ripcord.Tests;

namespace Ripcord.Tests.Diagnostics;

/// The sink itself. Plain file work, so it runs in the Linux container like everything else —
/// unlike the WMI adapter it replaces nothing that only exists on Windows.
public class FileDiagnosticLogTests : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());

    private readonly FixedClock clock = new(new DateTimeOffset(2026, 9, 14, 6, 0, 0, TimeSpan.Zero));

    private string At(string name) => System.IO.Path.Combine(this.directory, name);

    [Fact]
    public void A_line_is_appended_with_its_stamp()
    {
        FileDiagnosticLog log = new(
            this.clock, DiagnosticDestination.Default(this.At("ripcord.log")));

        log.Write(DiagnosticEntry.Of("status", "started"));
        log.Write(DiagnosticEntry.Of("status", "finished"));

        Assert.Equal(
            [
                "2026-09-14 06:00:00.000Z status: started",
                "2026-09-14 06:00:00.000Z status: finished",
            ],
            File.ReadAllLines(this.At("ripcord.log")));
    }

    [Fact]
    public void Switched_off_in_the_configuration_writes_nothing()
    {
        FileDiagnosticLog log = new(
            this.clock,
            DiagnosticDestination.Default(this.At("ripcord.log")) with { Enabled = false });

        log.Write(DiagnosticEntry.Of("status", "started"));

        Assert.False(File.Exists(this.At("ripcord.log")));
    }

    /// The path comes from a file that has not been read yet when the first line is written,
    /// so the log starts beside the binary and moves once it has.
    [Fact]
    public void The_destination_can_be_moved_after_the_first_line()
    {
        FileDiagnosticLog log = new(
            this.clock, DiagnosticDestination.Default(this.At("beside-the-binary.log")));

        log.Write(DiagnosticEntry.Of("status", "before"));
        log.SendTo(DiagnosticDestination.Default(this.At("chosen.log")));
        log.Write(DiagnosticEntry.Of("status", "after"));

        Assert.Contains("before", File.ReadAllText(this.At("beside-the-binary.log")));
        Assert.Contains("after", File.ReadAllText(this.At("chosen.log")));
    }

    /// One generation, replaced. The point is the bound, not the history: this file lives on
    /// the volume the VMs live on.
    [Fact]
    public void Past_its_size_the_file_is_rotated_once_and_no_further()
    {
        DiagnosticDestination small =
            DiagnosticDestination.Default(this.At("ripcord.log")) with { MaxBytes = 120 };

        FileDiagnosticLog log = new(this.clock, small);

        foreach (int line in Enumerable.Range(1, 6))
        {
            log.Write(DiagnosticEntry.Of("status", $"line {line}"));
        }

        Assert.True(File.Exists(this.At("ripcord.log.1")));
        Assert.False(File.Exists(this.At("ripcord.log.2")));
        Assert.Contains("line 6", File.ReadAllText(this.At("ripcord.log")));
        Assert.True(new FileInfo(this.At("ripcord.log")).Length <= small.MaxBytes);
    }

    /// The directory the operator pointed at may not exist: `D:\Ripcord` on a host where only
    /// the binary's own folder was ever created.
    [Fact]
    public void A_missing_directory_is_created_rather_than_reported()
    {
        FileDiagnosticLog log = new(
            this.clock, DiagnosticDestination.Default(this.At(@"nested/deeper/ripcord.log")));

        log.Write(DiagnosticEntry.Of("status", "started"));

        Assert.True(File.Exists(this.At(@"nested/deeper/ripcord.log")));
    }

    /// The rule this class is built around. A log that throws takes down the command it was
    /// written to explain, on the one host where nothing else can say what happened.
    [Fact]
    public void A_path_that_cannot_be_written_is_not_an_error()
    {
        FileDiagnosticLog log = new(
            this.clock, DiagnosticDestination.Default(this.directory));

        Directory.CreateDirectory(this.directory);

        log.Write(DiagnosticEntry.Of("status", "started"));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }
    }
}
