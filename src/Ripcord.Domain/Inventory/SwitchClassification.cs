namespace Ripcord.Domain.Inventory;

/// Whether a switch's kind can be trusted, given what the host's associations reported.
///
/// This is a judgement about the trustworthiness of an absence, not a CIM lookup, which is
/// why it is here rather than in the adapter: Private is deduced from a switch appearing in
/// neither traversal, and an empty traversal result is produced equally by "there is nothing
/// to find" and by "I looked in the wrong place". Those need telling apart, and telling them
/// apart is a decision.
public static class SwitchClassification
{
    /// Private is read off an absence — the switch binds neither a physical NIC nor the
    /// management OS — so it may only be concluded once the switch's own ports were actually
    /// enumerated. A traversal that returns nothing produces the same evidence as a switch
    /// with nothing attached, and guessing Private there is the reading that boots a test VM
    /// onto whatever it is really connected to.
    ///
    /// A switch with no ports at all is therefore unclassified rather than private: even a
    /// private switch carries the ports of the VMs on it, so an empty enumeration is a
    /// failure to look rather than a fact about the switch.
    public static SwitchConnectivity Of(
        bool reachesAPhysicalNic, bool reachesTheManagementOs, bool portsWereEnumerated)
    {
        if (reachesAPhysicalNic)
        {
            return SwitchConnectivity.External;
        }

        if (!portsWereEnumerated)
        {
            return SwitchConnectivity.Unknown;
        }

        return reachesTheManagementOs
            ? SwitchConnectivity.Internal
            : SwitchConnectivity.Private;
    }
}
