using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;

namespace Ripcord.Domain.Checks;

/// The four rules about how a replica would reach the network. All of them are about the
/// *target*'s copy of the VM, because that is the configuration it would boot with.
///
/// Adapters are paired across the two hosts by position. Nothing in CIM ties a replica's NIC
/// to the primary's, and the order is the order Hyper-V created them in — which replication
/// preserves. A VM whose NICs were added in a different order on the two sides would compare
/// the wrong pair, which is why a count mismatch is reported rather than silently zipped.
internal static class NetworkRules
{
    public static IEnumerable<Finding> Evaluate(CheckSubject subject)
    {
        foreach (VmSettings settings in subject.Configuration.Vms)
        {
            foreach (Finding finding in EvaluateVm(subject, settings.Name))
            {
                yield return finding;
            }
        }
    }

    private static IEnumerable<Finding> EvaluateVm(CheckSubject subject, string name)
    {
        if (CheckSubject.Find(subject.Target, name)?.Facts is not { } target)
        {
            return Unreadable(name, subject.Target.HostName, "the target's copy");
        }

        if (target.Adapters.Count == 0)
        {
            return
            [
                Found.Unevaluable(
                    CheckRules.ReplicaSwitchMismatch,
                    name,
                    $"no network adapter was read on {subject.Target.HostName}"),
            ];
        }

        VmFacts? source = CheckSubject.Find(subject.Source, name)?.Facts;

        List<Finding> findings = [];

        findings.AddRange(Attachment(subject, name, target));
        findings.AddRange(Macs(subject, name, target, source));
        findings.AddRange(Vlans(subject, name, target, source));

        return findings;
    }

    /// Attached to a switch that is not the expected one: the VM boots, reaches a network,
    /// and it is the wrong network — which the host-level checks would never notice.
    private static IEnumerable<Finding> Attachment(
        CheckSubject subject, string name, VmFacts target)
    {
        string expected = subject.Configuration.Replication.ExpectedSwitchName;

        foreach (VirtualAdapter adapter in target.Adapters)
        {
            // Attached to something the inventory could not name: not a fault, and not a
            // pass either. Reporting it as bound to nothing would be a critical nobody can
            // act on, and reporting nothing would be worse.
            if (adapter.SwitchName is null && adapter.IsConnected == true)
            {
                yield return Found.Unevaluable(
                    CheckRules.ReplicaSwitchMismatch,
                    name,
                    $"{adapter.Name} on {subject.Target.HostName} is attached to a switch "
                    + "that could not be named");
            }
            else if (adapter.SwitchName is null || adapter.IsConnected == false)
            {
                yield return Found.Violated(
                    CheckRules.ReplicaAdapterDisconnected,
                    name,
                    $"{adapter.Name} on {subject.Target.HostName} is bound to no switch",
                    "this VM boots with no network at all on the target: no clients, no "
                    + "domain, no monitoring",
                    $"Connect-VMNetworkAdapter -VMName {name} -Name '{adapter.Name}' "
                    + $"-SwitchName '{expected}'");
            }
            else if (!string.Equals(adapter.SwitchName, expected, StringComparison.OrdinalIgnoreCase))
            {
                yield return Found.Violated(
                    CheckRules.ReplicaSwitchMismatch,
                    name,
                    $"{adapter.Name} on {subject.Target.HostName} is on "
                    + $"'{adapter.SwitchName}', expected '{expected}'",
                    "this VM boots onto the wrong network on the target; the host-level "
                    + "switch check still passes, so nothing else would report it",
                    $"Connect-VMNetworkAdapter -VMName {name} -Name '{adapter.Name}' "
                    + $"-SwitchName '{expected}'");
            }
        }
    }

    /// A new MAC makes the guest see a brand-new adapter, so its static IP, DNS and gateway
    /// are gone. The VM boots, the vSwitch check passes, and nothing on the network answers.
    private static IEnumerable<Finding> Macs(
        CheckSubject subject, string name, VmFacts target, VmFacts? source)
    {
        for (int index = 0; index < target.Adapters.Count; index++)
        {
            VirtualAdapter adapter = target.Adapters[index];

            if (adapter.UsesDynamicMac == true)
            {
                yield return Found.Violated(
                    CheckRules.ReplicaMacDrift,
                    name,
                    $"{adapter.Name} on {subject.Target.HostName} uses a dynamic MAC address",
                    "the guest sees a brand-new adapter after failover, so its static IP, "
                    + "DNS and gateway are gone - it boots with an unconfigured NIC",
                    $"Set-VMNetworkAdapter -VMName {name} -Name '{adapter.Name}' "
                    + "-StaticMacAddress <the primary's address>");

                continue;
            }

            if (Counterpart(source, index) is not { } peer)
            {
                yield return Found.Unevaluable(
                    CheckRules.ReplicaMacDrift,
                    name,
                    $"{adapter.Name} has no counterpart to compare against on "
                    + subject.Source.HostName);

                continue;
            }

            if (!MacAddress.Known(adapter.MacAddress) || !MacAddress.Known(peer.MacAddress))
            {
                yield return Found.Unevaluable(
                    CheckRules.ReplicaMacDrift,
                    name,
                    $"the MAC address of {adapter.Name} was not readable on both hosts");
            }
            else if (!MacAddress.Same(adapter.MacAddress, peer.MacAddress))
            {
                yield return Found.Violated(
                    CheckRules.ReplicaMacDrift,
                    name,
                    $"{adapter.Name} is {adapter.MacAddress} on {subject.Target.HostName} "
                    + $"and {peer.MacAddress} on {subject.Source.HostName}",
                    "the guest sees a brand-new adapter after failover, so its static IP, "
                    + "DNS and gateway are gone - it boots with an unconfigured NIC",
                    $"Set-VMNetworkAdapter -VMName {name} -Name '{adapter.Name}' "
                    + $"-StaticMacAddress {peer.MacAddress}");
            }
        }
    }

    /// Correctly attached to a correctly named switch, on the wrong VLAN. Null is untagged on
    /// both sides, so an access port and a tagged port never compare equal.
    private static IEnumerable<Finding> Vlans(
        CheckSubject subject, string name, VmFacts target, VmFacts? source)
    {
        for (int index = 0; index < target.Adapters.Count; index++)
        {
            VirtualAdapter adapter = target.Adapters[index];

            if (Counterpart(source, index) is not { } peer)
            {
                yield return Found.Unevaluable(
                    CheckRules.VlanMismatch,
                    name,
                    $"{adapter.Name} has no counterpart to compare against on "
                    + subject.Source.HostName);
            }
            else if (adapter.VlanId != peer.VlanId)
            {
                yield return Found.Violated(
                    CheckRules.VlanMismatch,
                    name,
                    $"{adapter.Name} is on {Vlan(adapter.VlanId)} on "
                    + $"{subject.Target.HostName} and {Vlan(peer.VlanId)} on "
                    + subject.Source.HostName,
                    "this VM boots onto the wrong broadcast domain on the target; the "
                    + "switch is right and nothing else would report it",
                    $"Set-VMNetworkAdapterVlan -VMName {name} -VMNetworkAdapterName "
                    + $"'{adapter.Name}' -Access -VlanId {peer.VlanId}");
            }
        }
    }

    private static VirtualAdapter? Counterpart(VmFacts? source, int index) =>
        source is not null && index < source.Adapters.Count ? source.Adapters[index] : null;

    private static string Vlan(int? vlanId) =>
        vlanId is { } id ? $"VLAN {id}" : "no VLAN tag";

    /// One unevaluable finding per rule the missing data blocks: a single line saying "the
    /// network could not be checked" would hide which of the four answers is missing.
    private static IEnumerable<Finding> Unreadable(string name, string hostName, string what)
    {
        string reason = $"{what} of {name} was not readable on {hostName}";

        yield return Found.Unevaluable(CheckRules.ReplicaSwitchMismatch, name, reason);
        yield return Found.Unevaluable(CheckRules.ReplicaAdapterDisconnected, name, reason);
        yield return Found.Unevaluable(CheckRules.ReplicaMacDrift, name, reason);
        yield return Found.Unevaluable(CheckRules.VlanMismatch, name, reason);
    }
}
