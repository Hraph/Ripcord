namespace Ripcord.Domain.TestFailover;

/// What the integration services say about a test VM, mapped from
/// `Msvm_HeartbeatComponent.OperationalStatus[0]`.
///
/// The shape here is dictated by what Hyper-V will actually tell us, which is less than the
/// milestone assumed. Two documented facts drive it:
///
/// The component **does not exist while the VM is not running**, so its absence says nothing
/// about the guest — only that the machine is not up yet.
///
/// And status 12 is documented as "the guest service is not installed **or** has not yet been
/// contacted". Hyper-V itself conflates the two, so "this guest has no integration services"
/// is not a state Ripcord can report: it is indistinguishable from a slow boot until the
/// timeout expires. Claiming otherwise would be inventing a distinction the platform does not
/// make.
public enum Heartbeat
{
    /// The status could not be read, or carried a code this binary does not know.
    Unreadable,

    /// The guest cannot answer at all — an incompatible protocol version, or a paused VM.
    /// Waiting longer changes nothing.
    CannotConfirm,

    /// No heartbeat component, so the VM is not running yet.
    NotRunning,

    /// Running, and no answer — either still booting, or a guest with no integration
    /// services. Hyper-V does not distinguish the two.
    NoContact,

    Ok,
}
