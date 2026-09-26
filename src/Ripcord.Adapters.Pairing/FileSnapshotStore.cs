using Ripcord.Adapters.Pairing.Wire;
using Ripcord.Domain.Pairing;
using Ripcord.Ports.Pairing;

namespace Ripcord.Adapters.Pairing;

/// The snapshot on disk. The publishing service and an administrator's `ripcord` write it; the
/// unprivileged listener reads it and serves nothing else (decision D18).
public sealed class FileSnapshotStore : ISnapshotStore
{
    public void Write(string path, HostSnapshot snapshot)
    {
        // Serialise first: an unpublishable snapshot must not truncate the previous good one.
        string payload = SnapshotWireFormat.Write(snapshot);

        // A fresh host has no D:\Ripcord yet, and failing here would fail `ripcord status` on
        // a host that is otherwise perfectly healthy.
        if (System.IO.Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        // Written aside and moved into place: the listener reads this file while we rewrite
        // it, and must never see a half-written one. A name of its own per write: two writers
        // sharing one would move each other's half-written file.
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(temporary, payload);
            Replace(temporary, path);
        }
        finally
        {
            // A failed write is retried every 15 s: each one must not leave its own file behind.
            Discard(temporary);
        }
    }

    private const int ReplaceAttempts = 5;

    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(20);

    /// Windows refuses a replace, access denied, while another writer's replace of the same file
    /// is under way: the publisher and an administrator's `status` meet here.
    private static void Replace(string temporary, string path)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception exception) when (
                attempt < ReplaceAttempts && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(ReplaceRetryDelay);
            }
        }
    }

    private static void Discard(string temporary)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The write's own failure, if any, is the one worth reporting.
        }
    }

    /// Runs inside the network-facing service, so it never throws: no snapshot, an unreadable
    /// one and an unparseable one are all "nothing to serve".
    public HostSnapshot? Read(string path)
    {
        try
        {
            // Shared for delete: `ripcord status` replaces the file by moving over it, and a
            // read holding it without that share makes the replace fail.
            using FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);

            return SnapshotWireFormat.Read(reader.ReadToEnd());
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
