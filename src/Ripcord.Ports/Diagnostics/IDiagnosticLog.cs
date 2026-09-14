using Ripcord.Domain.Diagnostics;

namespace Ripcord.Ports.Diagnostics;

/// The file somebody reads when the console sentence was not enough.
///
/// **This never throws, and it never stops the command it is recording.** That is the opposite
/// of `IAuditLog`, and the difference is worth stating: an audit entry is part of moving
/// production, so a failover that cannot record what it knew must not start. A diagnostic line
/// is an explanation, and a host that refuses to run because it cannot explain itself has
/// turned the log into the outage.
public interface IDiagnosticLog
{
    void Write(DiagnosticEntry entry);

    /// Points the log at what the configuration asked for.
    ///
    /// The destination is in `ripcord.yaml`, and the first thing worth logging is often the
    /// reason that file could not be read — so the log starts beside the binary and is moved
    /// once the file has been looked at, rather than waiting for a configuration that may
    /// never become valid.
    void SendTo(DiagnosticDestination destination);
}
