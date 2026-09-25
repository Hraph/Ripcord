using Ripcord.Domain.Diagnostics;

namespace Ripcord.Ports.Diagnostics;

/// Reads the end of a log file, while the service may still be appending to it.
///
/// Never throws: null when the file is not there, a reading that says why when it cannot be
/// read. A report about a broken service must not break on the service's own file.
public interface IDiagnosticLogReader
{
    LogReading? Tail(string path, int maxLines);
}
