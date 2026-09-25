namespace Ripcord.Domain.Diagnostics;

/// Who is writing: a command someone ran, or the listener service. Each has its own daily
/// file, so the service's all-day story is not interleaved with every `ripcord status`.
public enum DiagnosticOrigin
{
    Command,
    Listener,
}
