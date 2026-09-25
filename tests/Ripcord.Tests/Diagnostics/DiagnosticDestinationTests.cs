using Ripcord.Domain.Configuration;
using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Diagnostics;

/// The one section of `ripcord.yaml` that refuses nothing. Everywhere else a value out of
/// range is an error, because it describes the pair; here it describes a log, and a host that
/// will not run because its log size is silly is a host with no log and no command.
public class DiagnosticDestinationTests
{
    private const string Logs = @"C:\Program Files\Ripcord\logs";

    [Fact]
    public void No_block_at_all_logs_into_the_logs_folder()
    {
        DiagnosticDestination destination = DiagnosticDestination.From(null, Logs);

        Assert.True(destination.Enabled);
        Assert.Equal(Logs, destination.Folder);
        Assert.Equal(5L * 1024 * 1024, destination.MaxBytes);
    }

    /// The listener and the dashboard are off until switched on; this is on until switched
    /// off, so a block that only moves the folder must not also silence it.
    [Fact]
    public void A_block_that_only_names_a_folder_leaves_the_log_on()
    {
        DiagnosticDestination destination = DiagnosticDestination.From(
            new DiagnosticsDocument { Path = @"D:\Ripcord\logs" }, Logs);

        Assert.True(destination.Enabled);
        Assert.Equal(@"D:\Ripcord\logs", destination.Folder);
    }

    /// `path` used to name the file; a configuration written then keeps working.
    [Theory]
    [InlineData(@"D:\Ripcord\ripcord.log", @"D:\Ripcord")]
    [InlineData(@"D:\Ripcord\Ripcord.LOG", @"D:\Ripcord")]
    [InlineData(@"D:\ripcord.log", @"D:\")]
    [InlineData("ripcord.log", Logs)]
    public void An_old_style_file_path_is_read_as_the_folder_that_holds_it(
        string path, string folder) =>
        Assert.Equal(
            folder,
            DiagnosticDestination.From(new DiagnosticsDocument { Path = path }, Logs).Folder);

    [Fact]
    public void The_log_can_be_switched_off_by_name() =>
        Assert.False(
            DiagnosticDestination.From(
                new DiagnosticsDocument { Enabled = false }, Logs).Enabled);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_falls_back_to_the_logs_folder(string? path) =>
        Assert.Equal(
            Logs,
            DiagnosticDestination.From(new DiagnosticsDocument { Path = path }, Logs).Folder);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_000)]
    public void A_size_out_of_range_is_replaced_rather_than_refused(int megabytes) =>
        Assert.Equal(
            5L * 1024 * 1024,
            DiagnosticDestination.From(
                new DiagnosticsDocument { MaxSizeMb = megabytes }, Logs).MaxBytes);

    [Fact]
    public void A_size_within_range_is_taken_as_written() =>
        Assert.Equal(
            20L * 1024 * 1024,
            DiagnosticDestination.From(
                new DiagnosticsDocument { MaxSizeMb = 20 }, Logs).MaxBytes);

    /// A command goes where the configuration asks; the service stays in the one folder it
    /// was granted, or it has no log at all.
    [Theory]
    [InlineData(DiagnosticOrigin.Command, @"D:\Elsewhere", false)]
    [InlineData(DiagnosticOrigin.Listener, Logs, true)]
    public void Only_a_command_follows_the_configuration(
        DiagnosticOrigin origin, string folder, bool enabled)
    {
        DiagnosticDestination asked =
            DiagnosticDestination.Default(@"D:\Elsewhere") with { Enabled = false };

        DiagnosticDestination applied = DiagnosticDestination.Default(Logs).Applied(origin, asked);

        Assert.Equal(folder, applied.Folder);
        Assert.Equal(enabled, applied.Enabled);
    }

    /// Decided before the write: a file allowed past its limit and trimmed afterwards is a
    /// file that was over the limit on the one run where the volume was full.
    [Theory]
    [InlineData(0, 100, false)]
    [InlineData(1_000, 100, false)]
    [InlineData(5L * 1024 * 1024, 1, true)]
    [InlineData(5L * 1024 * 1024 - 10, 20, true)]
    public void Rotation_is_decided_on_what_the_file_would_become(
        long existing, long incoming, bool expected) =>
        Assert.Equal(
            expected, DiagnosticDestination.Default(Logs).MustRotate(existing, incoming));
}
