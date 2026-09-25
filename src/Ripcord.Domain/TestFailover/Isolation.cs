using Ripcord.Domain.Inventory;

namespace Ripcord.Domain.TestFailover;

/// Why an adapter is not known to be isolated. The three are rendered differently and must
/// never be conflated: one says the adapter is wired to a switch nobody declared, one says
/// the declared switch itself reaches production, and one says nobody could establish what
/// the adapter is wired to. Reporting any of them as isolated is what starts the VM.
public enum IsolationDoubt
{
    Connected,
    ReachesProduction,
    Unreadable,
}

public sealed record IsolationBreach(string Adapter, string Observed, IsolationDoubt Doubt);

/// Whether a test VM can be started without putting a duplicate identity on the production
/// network. Isolated means every adapter was positively established to carry nothing.
public sealed record IsolationAssessment(IReadOnlyList<IsolationBreach> Breaches)
{
    public bool IsIsolated => this.Breaches.Count == 0;
}

/// A test VM inherits the replica's adapters, and a milestone 2 critical rule requires those
/// to point at the production switch. So the default state of a test VM is *wrong*, and this
/// is the rule that catches it before anything boots.
///
/// An adapter carries nothing when it names no switch, is explicitly unplugged, or sits on
/// the switch the operator declared for test failovers *and* that switch is not External.
/// Both halves of the last one are load-bearing. Name equality alone would clear a second
/// External switch — a DMZ, a management network, the one a multi-homed host always has —
/// which reaches production just as well as the switch the configuration was checked against.
/// Every other answer, including no answer, is a breach.
public static class Isolation
{
    public static IsolationAssessment Of(
        IReadOnlyList<VirtualAdapter>? adapters,
        string? testFailoverSwitch,
        IReadOnlyList<HostSwitch> switches)
    {
        ArgumentNullException.ThrowIfNull(switches);

        // Null is "the adapters could not be read", which an empty list would silently
        // report as a VM with no network at all — the reassuring false negative.
        if (adapters is null)
        {
            return new IsolationAssessment([
                new IsolationBreach(
                    "(all)",
                    "the virtual adapters of this VM could not be read",
                    IsolationDoubt.Unreadable),
            ]);
        }

        return new IsolationAssessment(
            [.. adapters
                .Select(adapter => Judge(adapter, testFailoverSwitch, switches))
                .OfType<IsolationBreach>()]);
    }

    /// The adapters not on the switch the operator declared for test failovers. Isolated, but
    /// a test that boots with no network does not test what `test_failover_switch` set up.
    /// Empty when no switch was declared: every adapter is then meant to be on nothing.
    public static IReadOnlyList<string> OffTheTestSwitch(
        IReadOnlyList<VirtualAdapter>? adapters, string? testFailoverSwitch) =>
        testFailoverSwitch is null || adapters is null
            ? []
            : [.. adapters
                .Where(adapter => adapter.IsConnected == false
                    || adapter.SwitchName is not { } name
                    || !Declared(name, testFailoverSwitch))
                .Select(adapter => adapter.Name)];

    private static IsolationBreach? Judge(
        VirtualAdapter adapter, string? testFailoverSwitch, IReadOnlyList<HostSwitch> switches)
    {
        if (adapter.SwitchName is not { } switchName)
        {
            return null;
        }

        if (adapter.IsConnected == false)
        {
            return null;
        }

        return Declared(switchName, testFailoverSwitch)
            ? OnTheDeclaredSwitch(adapter, switchName, switches)
            : OnSomeOtherSwitch(adapter, switchName);
    }

    /// The adapter is where the operator said test VMs go. That is necessary and not
    /// sufficient: the switch still has to be one that cannot reach a physical NIC.
    private static IsolationBreach? OnTheDeclaredSwitch(
        VirtualAdapter adapter, string switchName, IReadOnlyList<HostSwitch> switches)
    {
        SwitchConnectivity connectivity = switches
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Name, switchName, StringComparison.OrdinalIgnoreCase))
            ?.Connectivity
            ?? SwitchConnectivity.Unknown;

        return connectivity switch
        {
            SwitchConnectivity.Internal or SwitchConnectivity.Private => null,

            SwitchConnectivity.External => new IsolationBreach(
                adapter.Name,
                $"on '{switchName}', which is an external switch and reaches the physical "
                    + "network",
                IsolationDoubt.ReachesProduction),

            _ => new IsolationBreach(
                adapter.Name,
                $"on '{switchName}', whose kind could not be established",
                IsolationDoubt.Unreadable),
        };
    }

    /// Not the declared switch. Whether it reaches production is beside the point: a test VM
    /// somewhere nobody named is a test VM nobody expected to be there.
    private static IsolationBreach OnSomeOtherSwitch(VirtualAdapter adapter, string switchName) =>
        adapter.IsConnected == true
            ? new IsolationBreach(
                adapter.Name,
                $"connected to '{switchName}'",
                IsolationDoubt.Connected)

            // V6: a named switch whose connection flag came back unread. Treating it as
            // unplugged is the one reading that boots a duplicate domain controller.
            : new IsolationBreach(
                adapter.Name,
                $"on '{switchName}', and whether it is plugged in could not be read",
                IsolationDoubt.Unreadable);

    private static bool Declared(string switchName, string? testFailoverSwitch) =>
        testFailoverSwitch is not null
        && string.Equals(switchName, testFailoverSwitch, StringComparison.OrdinalIgnoreCase);
}
