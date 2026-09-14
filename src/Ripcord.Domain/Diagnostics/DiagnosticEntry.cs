using System.Globalization;

namespace Ripcord.Domain.Diagnostics;

/// One thing that happened, written for somebody who is not standing at the console.
///
/// The console gets one sentence, because a KVM at 1024×768 during an incident has room for
/// one sentence. This is where the rest goes — the exception type, the stack, the WMI error
/// code — so that the operator can send a file rather than retype what they saw.
///
/// `Detail` is whatever the failure knew: an `exception.ToString()` normally. It is never
/// parsed, only written, so nothing depends on its shape.
public sealed record DiagnosticEntry(
    string Operation, string Message, IReadOnlyList<string> Detail)
{
    public static DiagnosticEntry Of(string operation, string message) =>
        new(operation, message, []);

    /// The failure as it was caught. Split here rather than at the sink so the log holds one
    /// line per line of a stack trace whatever wrote it.
    public static DiagnosticEntry Of(string operation, string message, string detail) =>
        new(operation, message, Lines(detail));

    /// The exact text written, one string per line, newline-free and free of anything that
    /// could move a cursor — this file is read with `type` on the same console every other
    /// rule protects.
    public IReadOnlyList<string> Render(DateTimeOffset at)
    {
        List<string> rendered =
        [
            string.Create(
                CultureInfo.InvariantCulture,
                $"{at.ToUniversalTime():yyyy-MM-dd HH:mm:ss.fff}Z {Field(this.Operation)}: "
                    + $"{Field(this.Message)}"),
        ];

        // Indented, so a stack trace is visibly subordinate to the line it explains and a
        // reader skimming for timestamps skims down one column.
        rendered.AddRange(this.Detail.Select(line => "    " + Field(line)));

        return rendered;
    }

    private static string Field(string text) => Printable.Of(Redaction.Scrub(text));

    private static string[] Lines(string detail) =>
        detail.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    public bool Equals(DiagnosticEntry? other) =>
        other is not null
        && this.Operation == other.Operation
        && this.Message == other.Message
        && Structural.Same(this.Detail, other.Detail);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(this.Operation);
        hash.Add(this.Message);
        Structural.Add(ref hash, this.Detail);
        return hash.ToHashCode();
    }
}
