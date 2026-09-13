using Ripcord.Domain.Audit;

namespace Ripcord.Ports.Audit;

/// Appends one line to the audit trail (decision D8).
///
/// It appends and nothing else: no read, no seek, no rewrite. The trail is append-only by an
/// ACL set at install time, so a port able to rewrite would be a port whose implementation the
/// host would have to refuse anyway.
///
/// **This throws when it cannot write, and callers must not swallow it.** An audit entry is not
/// decoration on a mutating failover — it is the record of what was known before production was
/// moved, and a failover that cannot say afterwards what it could not see beforehand should not
/// start.
public interface IAuditLog
{
    void Append(AuditEntry entry);
}
