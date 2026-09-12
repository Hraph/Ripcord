namespace Ripcord.Domain.Inventory;

/// What a virtual switch can reach. External is the only kind bridged to a physical NIC, so
/// it is the only kind from which a VM can reach the production network; Internal reaches the
/// management OS, Private reaches nothing but VMs on the same switch.
///
/// Determined from the switch's external-port association rather than from its name, for the
/// same reason milestone 3 refuses to match test VMs on a `" - Test"` suffix: a name is a
/// convention and a convention is not a guarantee. Unknown means the classification could not
/// be established, which is never the same as safe.
public enum SwitchConnectivity
{
    Unknown,
    External,
    Internal,
    Private,
}

public sealed record HostSwitch(string Name, SwitchConnectivity Connectivity);
