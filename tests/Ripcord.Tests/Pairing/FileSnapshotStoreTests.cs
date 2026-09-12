using Ripcord.Adapters.Pairing;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Pairing;

/// One file, written by the privileged process and read by the unprivileged one. Reading is
/// the side that runs inside a network-facing service, so it never throws.
public sealed class FileSnapshotStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    private readonly string directory =
        Directory.CreateTempSubdirectory("ripcord-snapshot-tests").FullName;

    [Fact]
    public void A_written_snapshot_reads_back_unchanged()
    {
        FileSnapshotStore store = Store();
        HostSnapshot snapshot = Snapshot();

        store.Write(this.Path, snapshot);

        Assert.Equal(snapshot, store.Read(this.Path));
    }

    [Fact]
    public void Reading_before_anything_was_written_yields_null()
    {
        Assert.Null(Store().Read(this.Path));
    }

    /// The service reads a file another process rewrites underneath it. Garbage must read as
    /// "nothing to serve", never as an exception inside the listener.
    [Fact]
    public void Reading_a_corrupt_file_yields_null_rather_than_throwing()
    {
        File.WriteAllText(this.Path, "{ this is not a snapshot");

        Assert.Null(Store().Read(this.Path));
    }

    [Fact]
    public void Reading_an_empty_file_yields_null()
    {
        File.WriteAllText(this.Path, "");

        Assert.Null(Store().Read(this.Path));
    }

    /// A reader catching a half-written file would serve a truncated snapshot or nothing at
    /// all. The write lands atomically so the reader only ever sees a complete one.
    [Fact]
    public void A_rewrite_replaces_the_file_atomically()
    {
        FileSnapshotStore store = Store();
        store.Write(this.Path, Snapshot());
        store.Write(this.Path, Snapshot(Now.AddMinutes(5)));

        Assert.Equal(Now.AddMinutes(5), store.Read(this.Path)!.CapturedAt);
        Assert.Empty(Directory.GetFiles(this.directory, "*.tmp"));
    }

    /// The directory is created on first write: on a fresh host nobody has made D:\Ripcord yet,
    /// and failing there would mean `ripcord status` fails on a host that is otherwise fine.
    [Fact]
    public void Writing_creates_the_directory_when_it_is_missing()
    {
        string nested = System.IO.Path.Combine(this.directory, "does", "not", "exist", "state.json");

        new FileSnapshotStore().Write(nested, Snapshot());

        Assert.True(File.Exists(nested));
    }

    /// A host that cannot read its own Hyper-V must not publish a confident empty inventory.
    [Fact]
    public void An_unreachable_state_is_not_published()
    {
        FileSnapshotStore store = Store();

        Assert.Throws<ArgumentException>(() => store.Write(
            this.Path,
            new HostSnapshot(
                Now,
                HostState.Unreachable("HV-REPLICA-01", HostReachability.Failed("WMI down", Now)))));

        Assert.False(File.Exists(this.Path));
    }

    private string Path => System.IO.Path.Combine(this.directory, "state.json");

    private static FileSnapshotStore Store() => new();

    private static HostSnapshot Snapshot(DateTimeOffset? capturedAt = null) =>
        new(
            capturedAt ?? Now,
            new HostState(
                "HV-REPLICA-01",
                [
                    new VmReplicationState(
                        "VM-DC-01",
                        ReplicationRole.Replica,
                        ReplicationState.Replicating,
                        ReplicationHealth.Normal,
                        Now.AddSeconds(-20),
                        2048),
                ],
                HostReachability.Reachable()));

    public void Dispose() => Directory.Delete(this.directory, recursive: true);
}
