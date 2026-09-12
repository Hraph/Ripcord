using Ripcord.Domain.Inventory;

namespace Ripcord.Domain.TestFailover;

/// Why an adapter is not known to be isolated. The two are rendered differently and must
/// never be conflated: one says the adapter is wired to a live switch, the other says nobody
/// could establish what it is wired to. Reporting the second as the first would be a lie the
/// operator could act on; reporting it as isolated would be the lie that starts the VM.
public enum IsolationDoubt
{
    Connected,
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
/// An adapter carries nothing when it names no switch, names the configured test switch, or
/// is explicitly unplugged. Every other answer — including no answer — is a breach.
public static class Isolation
{
    public static IsolationAssessment Of(
        IReadOnlyList<VirtualAdapter>? adapters, string? testFailoverSwitch)
    {
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
                .Select(adapter => Judge(adapter, testFailoverSwitch))
                .OfType<IsolationBreach>()]);
    }

    private static IsolationBreach? Judge(VirtualAdapter adapter, string? testFailoverSwitch)
    {
        if (adapter.SwitchName is not { } switchName)
        {
            return null;
        }

        if (testFailoverSwitch is not null
            && string.Equals(switchName, testFailoverSwitch, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return adapter.IsConnected switch
        {
            false => null,
            true => new IsolationBreach(
                adapter.Name,
                $"connected to '{switchName}'",
                IsolationDoubt.Connected),

            // V6 again: a named switch whose connection flag came back unread. Treating it
            // as unplugged is the one reading that boots a duplicate domain controller.
            null => new IsolationBreach(
                adapter.Name,
                $"on '{switchName}', and whether it is plugged in could not be read",
                IsolationDoubt.Unreadable),
        };
    }
}
