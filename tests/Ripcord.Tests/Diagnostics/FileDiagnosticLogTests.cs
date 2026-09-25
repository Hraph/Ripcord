using Ripcord.Adapters.Diagnostics;
using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Diagnostics;

/// The sink itself. Plain file work, so it runs in the Linux container like everything else —
/// unlike the WMI adapter it replaces nothing that only exists on Windows.
public class FileDiagnosticLogTests : IDisposable
{
    private const string Today = "ripcord-2026-09-14.log";

    private readonly string directory = Path.Combine(
        Path.GetTempPath(), Path.GetRandomFileName());

    private readonly MovableClock clock =
        new(new DateTimeOffset(2026, 9, 14, 6, 0, 0, TimeSpan.Zero));

    private string At(string name) => Path.Combine(this.directory, name);

    private FileDiagnosticLog Log(
        DiagnosticOrigin origin = DiagnosticOrigin.Command, string? folder = null) =>
        new(this.clock, origin, DiagnosticDestination.Default(folder ?? this.directory));

    [Fact]
    public void A_line_is_appended_to_the_day_s_file_with_its_stamp()
    {
        FileDiagnosticLog log = this.Log();

        log.Write(DiagnosticEntry.Of("status", "started"));
        log.Write(DiagnosticEntry.Of("status", "finished"));

        Assert.Equal(
            [
                "2026-09-14 06:00:00.000Z status: started",
                "2026-09-14 06:00:00.000Z status: finished",
            ],
            File.ReadAllLines(this.At(Today)));
        Assert.Equal(this.At(Today), log.CurrentFile);
    }

    [Fact]
    public void Each_origin_writes_its_own_dated_file()
    {
        this.Log().Write(DiagnosticEntry.Of("status", "a command"));
        this.Log(DiagnosticOrigin.Listener).Write(DiagnosticEntry.Of("serve", "the service"));

        Assert.Contains("a command", File.ReadAllText(this.At(Today)));
        Assert.Contains("the service", File.ReadAllText(this.At("listener-2026-09-14.log")));
    }

    /// The service runs for weeks with one log object; the file still changes at midnight UTC.
    [Fact]
    public void A_new_utc_day_starts_a_new_file()
    {
        FileDiagnosticLog log = this.Log(DiagnosticOrigin.Listener);

        log.Write(DiagnosticEntry.Of("listener", "before midnight"));
        this.clock.UtcNow = this.clock.UtcNow.AddDays(1);
        log.Write(DiagnosticEntry.Of("listener", "after midnight"));

        Assert.DoesNotContain(
            "after midnight", File.ReadAllText(this.At("listener-2026-09-14.log")));
        Assert.Contains("after midnight", File.ReadAllText(this.At("listener-2026-09-15.log")));
    }

    [Fact]
    public void Old_files_are_pruned_on_the_first_write_and_others_left_alone()
    {
        Directory.CreateDirectory(this.directory);
        string[] expired = ["ripcord-2026-08-01.log", "listener-2026-08-15.log.1"];
        string[] kept = ["ripcord-2026-08-16.log", "ripcord.log", "notes.txt"];

        foreach (string name in expired.Concat(kept))
        {
            File.WriteAllText(this.At(name), "old");
        }

        this.Log().Write(DiagnosticEntry.Of("status", "started"));

        Assert.All(expired, name => Assert.False(File.Exists(this.At(name)), name));
        Assert.All(kept, name => Assert.True(File.Exists(this.At(name)), name));
    }

    [Fact]
    public void A_folder_named_like_an_old_log_is_left_alone()
    {
        Directory.CreateDirectory(this.At("listener-2020-01-01.log"));

        this.Log().Write(DiagnosticEntry.Of("status", "started"));

        Assert.True(Directory.Exists(this.At("listener-2020-01-01.log")));
        Assert.True(File.Exists(this.At(Today)));
    }

    [Fact]
    public void Switched_off_in_the_configuration_writes_nothing()
    {
        FileDiagnosticLog log = this.Log();
        log.SendTo(DiagnosticDestination.Default(this.directory) with { Enabled = false });

        log.Write(DiagnosticEntry.Of("status", "started"));

        Assert.False(Directory.Exists(this.directory));
    }

    /// The folder comes from a file that has not been read yet when the first line is
    /// written, so the log starts in the logs folder and moves once it has.
    [Fact]
    public void The_folder_can_be_moved_after_the_first_line()
    {
        FileDiagnosticLog log = this.Log(folder: this.At("logs"));

        log.Write(DiagnosticEntry.Of("status", "before"));
        log.SendTo(DiagnosticDestination.Default(this.At("chosen")));
        log.Write(DiagnosticEntry.Of("status", "after"));

        Assert.Contains("before", File.ReadAllText(this.At(Path.Combine("logs", Today))));
        Assert.Contains("after", File.ReadAllText(this.At(Path.Combine("chosen", Today))));
    }

    [Fact]
    public void The_listener_stays_in_its_granted_folder()
    {
        FileDiagnosticLog log = this.Log(DiagnosticOrigin.Listener);

        log.SendTo(DiagnosticDestination.Default(this.At("chosen")));
        log.Write(DiagnosticEntry.Of("serve", "running"));

        Assert.True(File.Exists(this.At("listener-2026-09-14.log")));
        Assert.False(Directory.Exists(this.At("chosen")));
    }

    /// One extra file a day, replaced. The point is the bound, not the history: this folder
    /// lives on the volume the VMs live on.
    [Fact]
    public void Past_its_size_the_day_s_file_is_rotated_once_and_no_further()
    {
        DiagnosticDestination small =
            DiagnosticDestination.Default(this.directory) with { MaxBytes = 120 };

        FileDiagnosticLog log = new(this.clock, DiagnosticOrigin.Command, small);

        foreach (int line in Enumerable.Range(1, 6))
        {
            log.Write(DiagnosticEntry.Of("status", $"line {line}"));
        }

        Assert.True(File.Exists(this.At(Today + ".1")));
        Assert.False(File.Exists(this.At(Today + ".2")));
        Assert.Contains("line 6", File.ReadAllText(this.At(Today)));
        Assert.True(new FileInfo(this.At(Today)).Length <= small.MaxBytes);
    }

    /// The folder the operator pointed at may not exist: `D:\Ripcord` on a host where only
    /// the binary's own folder was ever created.
    [Fact]
    public void A_missing_folder_is_created_rather_than_reported()
    {
        this.Log(folder: this.At(Path.Combine("nested", "deeper")))
            .Write(DiagnosticEntry.Of("status", "started"));

        Assert.True(File.Exists(this.At(Path.Combine("nested", "deeper", Today))));
    }

    /// The rule this class is built around. A log that throws takes down the command it was
    /// written to explain, on the one host where nothing else can say what happened. The
    /// listener still needs the reason, to put it in the event log instead.
    [Fact]
    public void A_folder_that_cannot_be_created_is_reported_not_thrown()
    {
        Directory.CreateDirectory(this.directory);
        File.WriteAllText(this.At("a-file"), "");

        FileDiagnosticLog log = this.Log(folder: this.At("a-file"));

        log.Write(DiagnosticEntry.Of("status", "started"));

        Assert.NotNull(log.TryWrite(DiagnosticEntry.Of("status", "started")));
    }

    [Fact]
    public void A_written_line_reports_no_failure() =>
        Assert.Null(this.Log().TryWrite(DiagnosticEntry.Of("status", "started")));

    [Fact]
    public void A_text_writer_turns_each_complete_line_into_one_entry()
    {
        RecordingDiagnosticLog log = new();

        using (DiagnosticTextWriter writer = new(log, "serve"))
        {
            writer.Write("listening ");
            writer.Write("on 7443");
            writer.WriteLine();
            writer.WriteLine("second");
            writer.WriteLine();
        }

        Assert.Equal(
            [DiagnosticEntry.Of("serve", "listening on 7443"), DiagnosticEntry.Of("serve", "second")],
            log.Written);
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
