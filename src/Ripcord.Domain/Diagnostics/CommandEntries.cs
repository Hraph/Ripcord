using System.Globalization;

namespace Ripcord.Domain.Diagnostics;

/// The lines every command writes about how it ended. Written and read back here, so
/// `ripcord service` cannot drift from what the listener actually logged.
public static class CommandEntries
{
    private const string ExitPrefix = "exit ";

    private const string CancelledMessage = "cancelled";

    private const string CrashMessage = "the command stopped on an unhandled error";

    public static DiagnosticEntry Exited(string operation, ExitCode code) =>
        DiagnosticEntry.Of(
            operation,
            string.Create(CultureInfo.InvariantCulture, $"{ExitPrefix}{(int)code}: {code}"));

    public static DiagnosticEntry Cancelled(string operation) =>
        DiagnosticEntry.Of(operation, CancelledMessage);

    public static DiagnosticEntry Crashed(string operation, string detail) =>
        DiagnosticEntry.Of(operation, CrashMessage, detail);

    /// `exit N: Name` exactly, so a console line that merely mentions an exit is not read as one.
    public static ExitCode? ExitIn(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!message.StartsWith(ExitPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        int colon = message.IndexOf(": ", StringComparison.Ordinal);

        return colon > ExitPrefix.Length
            && int.TryParse(
                message[ExitPrefix.Length..colon],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int number)
            && Enum.IsDefined((ExitCode)number)
            && message[(colon + 2)..] == ((ExitCode)number).ToString()
                ? (ExitCode)number
                : null;
    }

    public static bool IsCancelled(string message) => message == CancelledMessage;

    public static bool IsCrash(string message) => message == CrashMessage;
}
