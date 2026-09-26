using Ripcord.Domain.Diagnostics;
using Ripcord.Ports;
using Ripcord.Ports.Diagnostics;

namespace Ripcord.Adapters.Diagnostics;

/// The publishing service's log: every line goes through `RepeatedEntries`, so a failure that
/// comes back every fifteen seconds is written once an hour rather than every time.
public sealed class RepeatLimitedDiagnosticLog(
    IDiagnosticLog inner, IClock clock, TimeSpan window, IReadOnlyList<string>? exempt = null)
    : IDiagnosticLog
{
    private readonly RepeatedEntries repeated = new(window, exempt);

    private readonly Lock gate = new();

    public void Write(DiagnosticEntry entry)
    {
        IReadOnlyList<DiagnosticEntry> admitted;

        lock (this.gate)
        {
            admitted = this.repeated.Admit(entry, clock.UtcNow);
        }

        foreach (DiagnosticEntry line in admitted)
        {
            inner.Write(line);
        }
    }

    public void SendTo(DiagnosticDestination destination) => inner.SendTo(destination);
}
