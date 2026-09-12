using Ripcord.Domain.Pairing;

namespace Ripcord.Ports.Pairing;

/// The file the privileged `ripcord` writes and the unprivileged listener serves. Two
/// processes, one file, opposite privileges — which is the whole point of decision D18.
public interface ISnapshotStore
{
    void Write(string path, HostSnapshot snapshot);

    /// Null when there is no snapshot yet, or it cannot be read or understood. A listener
    /// with nothing to serve answers nothing; it does not invent a state.
    HostSnapshot? Read(string path);
}
