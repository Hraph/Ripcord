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
    /// Only the external traversal has to be proven, and the asymmetry is the point: External
    /// is the sole dangerous value, and Internal and Private are treated identically by the
    /// isolation rule. With external proven, "not External" holds whatever the internal
    /// traversal did; without it, nothing can be trusted and the switch is unclassified.
    ///
    /// Letting the internal traversal help prove the pair would only loosen this. The two
    /// share their association classes and differ in the port class, so the half that can
    /// fail on its own is exactly the half that matters.
    public static SwitchConnectivity Of(
        bool reachesAPhysicalNic, bool reachesTheManagementOs, bool externalTraversalProven)
    {
        if (reachesAPhysicalNic)
        {
            return SwitchConnectivity.External;
        }

        if (!externalTraversalProven)
        {
            return SwitchConnectivity.Unknown;
        }

        return reachesTheManagementOs
            ? SwitchConnectivity.Internal
            : SwitchConnectivity.Private;
    }
}
