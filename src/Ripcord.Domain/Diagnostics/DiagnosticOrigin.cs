namespace Ripcord.Domain.Diagnostics;

/// Who is writing: a command someone ran, the listener service or the publishing service. Each
/// has its own daily file, so a service's all-day story is not interleaved with every
/// `ripcord status`.
public enum DiagnosticOrigin
{
    Command,
    Listener,
    Publisher,
}
