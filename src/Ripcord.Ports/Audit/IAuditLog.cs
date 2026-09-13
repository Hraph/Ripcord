using Ripcord.Domain.Audit;

namespace Ripcord.Ports.Audit;

/// Appends one line to the audit trail (decision D8).
///
/// It appends and nothing else: no read, no seek, no rewrite. That is the port's own guarantee.
/// Immutability against whoever administers the host is a separate matter and not this port's
/// to give: it needs an ACL applied to the file at install time, nothing here applies one, and
/// whether the shape that ACL should take is genuinely append-only is still open (V13).
///
/// **This throws when it cannot write, and callers must not swallow it.** An audit entry is not
/// decoration on a mutating failover — it is the record of what was known before production was
/// moved, and a failover that cannot say afterwards what it could not see beforehand should not
/// start.
public interface IAuditLog
{
    void Append(AuditEntry entry);
}
