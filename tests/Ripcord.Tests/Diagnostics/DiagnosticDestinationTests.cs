using Ripcord.Domain.Configuration;
using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Diagnostics;

/// The one section of `ripcord.yaml` that refuses nothing. Everywhere else a value out of
/// range is an error, because it describes the pair; here it describes a log file, and a host
/// that will not run because its log size is silly is a host with no log and no command.
public class DiagnosticDestinationTests
{
    private const string Beside = @"C:\Program Files\Ripcord\ripcord.log";

    [Fact]
    public void No_block_at_all_logs_beside_the_binary()
    {
        DiagnosticDestination destination = DiagnosticDestination.From(null, Beside);

        Assert.True(destination.Enabled);
        Assert.Equal(Beside, destination.Path);
        Assert.Equal(5L * 1024 * 1024, destination.MaxBytes);
    }

    /// The listener and the dashboard are off until switched on; this is on until switched
    /// off, so a block that only moves the file must not also silence it.
    [Fact]
    public void A_block_that_only_names_a_path_leaves_the_log_on()
    {
        DiagnosticDestination destination = DiagnosticDestination.From(
            new DiagnosticsDocument { Path = @"D:\Ripcord\ripcord.log" }, Beside);

        Assert.True(destination.Enabled);
        Assert.Equal(@"D:\Ripcord\ripcord.log", destination.Path);
    }

    [Fact]
    public void The_log_can_be_switched_off_by_name() =>
        Assert.False(
            DiagnosticDestination.From(
                new DiagnosticsDocument { Enabled = false }, Beside).Enabled);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_falls_back_beside_the_binary(string? path) =>
        Assert.Equal(
            Beside,
            DiagnosticDestination.From(new DiagnosticsDocument { Path = path }, Beside).Path);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_000)]
    public void A_size_out_of_range_is_replaced_rather_than_refused(int megabytes) =>
        Assert.Equal(
            5L * 1024 * 1024,
            DiagnosticDestination.From(
                new DiagnosticsDocument { MaxSizeMb = megabytes }, Beside).MaxBytes);

    [Fact]
    public void A_size_within_range_is_taken_as_written() =>
        Assert.Equal(
            20L * 1024 * 1024,
            DiagnosticDestination.From(
                new DiagnosticsDocument { MaxSizeMb = 20 }, Beside).MaxBytes);

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
            expected, DiagnosticDestination.Default(Beside).MustRotate(existing, incoming));

    /// Two files and no more. A series of numbered logs is the thing that fills the volume it
    /// was written to explain.
    [Fact]
    public void The_previous_file_sits_beside_the_current_one() =>
        Assert.Equal(Beside + ".1", DiagnosticDestination.Default(Beside).PreviousPath);
}
