namespace Ripcord.Domain.TestFailover;

/// What the integration services say about a running test VM.
///
/// The instance only exists while the VM is running, and it requires the guest to have the
/// integration services installed — so it is not a valid readiness gate for a guest that
/// lacks them. `NotInstalled` is therefore a distinct answer from `NoContact`: one says the
/// VM cannot be asked, the other says it was asked and has not answered yet. Reporting the
/// first as a pass would be the tool claiming a boot it never observed.
public enum Heartbeat
{
    Unreadable,
    NotInstalled,
    NoContact,
    Ok,
}
