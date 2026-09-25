using System.Text;
using Ripcord.Domain.Diagnostics;
using Ripcord.Ports.Diagnostics;

namespace Ripcord.Adapters.Diagnostics;

/// A verb's console output turned into diagnostic entries, one per line. The listener service
/// has no console, so this is where what `ripcord serve` would have printed goes.
public sealed class DiagnosticTextWriter(IDiagnosticLog log, string operation) : TextWriter
{
    private readonly Lock gate = new();

    private readonly StringBuilder pending = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        string? line = null;

        lock (this.gate)
        {
            if (value == '\n')
            {
                line = this.pending.ToString().TrimEnd('\r');
                this.pending.Clear();
            }
            else
            {
                this.pending.Append(value);
            }
        }

        if (line is { Length: > 0 })
        {
            log.Write(DiagnosticEntry.Of(operation, line));
        }
    }

    public override void Write(string? value)
    {
        foreach (char character in value ?? "")
        {
            this.Write(character);
        }
    }
}
