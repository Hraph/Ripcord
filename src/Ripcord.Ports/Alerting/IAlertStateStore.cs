using Ripcord.Domain.Alerting;

namespace Ripcord.Ports.Alerting;

/// What the last run left behind, across process boundaries: the scheduled task is a new
/// process every time, so "have I already said this" is a file.
///
/// Read never throws. A state file that cannot be read is a first run — the cost is one
/// duplicate notification, where a throw would be a `ripcord check` that fails on a file
/// nobody knew existed.
public interface IAlertStateStore
{
    AlertState Read();

    void Write(AlertState state);
}
