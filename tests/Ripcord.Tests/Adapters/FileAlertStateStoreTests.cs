using Ripcord.Adapters.Notify;
using Ripcord.Domain.Alerting;

namespace Ripcord.Tests.Adapters;

/// The file the scheduled task's memory lives in. It is the only thing standing between one
/// notification and one every fifteen minutes, so its failure modes are the interesting part.
public class FileAlertStateStoreTests : IDisposable
{
    private readonly string directory =
        System.IO.Path.Combine(Path.GetTempPath(), $"ripcord-alert-{Guid.NewGuid():N}");

    [Fact]
    public void A_state_written_is_the_state_read_back()
    {
        FileAlertStateStore store = new(this.StatePath());
        AlertState state = new(
            "replica-switch-mismatch VM-DC-01",
            new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.FromHours(2)),
            null);

        store.Write(state);

        Assert.Equal(state, store.Read());
    }

    [Fact]
    public void A_host_that_has_never_notified_reads_as_clear() =>
        Assert.Equal(AlertState.Clear, new FileAlertStateStore(this.StatePath()).Read());

    /// One duplicate notification is the cost; a `ripcord check` that fails over its own
    /// bookkeeping is not a trade worth making.
    [Fact]
    public void A_file_that_cannot_be_parsed_reads_as_clear()
    {
        string path = this.StatePath();
        Directory.CreateDirectory(this.directory);
        File.WriteAllText(path, "{ not json");

        Assert.Equal(AlertState.Clear, new FileAlertStateStore(path).Read());
    }

    /// It is read by a human after a silent night, so the findings are a list of rule ids and
    /// not a hash of them.
    [Fact]
    public void The_file_names_the_findings_it_is_remembering()
    {
        string path = this.StatePath();
        new FileAlertStateStore(path).Write(
            new AlertState("replica-switch-mismatch VM-DC-01", null, null));

        Assert.Contains(
            "replica-switch-mismatch VM-DC-01", File.ReadAllText(path), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
        {
            Directory.Delete(this.directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string StatePath() => System.IO.Path.Combine(this.directory, "alert-state.json");
}
