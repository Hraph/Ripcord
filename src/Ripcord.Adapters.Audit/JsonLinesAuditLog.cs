using System.Text;
using System.Text.Json;
using Ripcord.Domain.Audit;
using Ripcord.Ports.Audit;

namespace Ripcord.Adapters.Audit;

/// The audit trail as JSON Lines (decision D8): one self-contained object per line, appended and
/// never rewritten.
///
/// One line per event rather than a JSON document, because a document has to be closed to be
/// valid. A host that loses power mid-failover leaves a truncated final line and every line
/// before it still readable — which is the case this file exists for.
///
/// **Immutability is not enforced here and cannot be.** It would come from an append-only ACL
/// applied to the file at install time — which nothing in Ripcord applies, and whose shape is
/// still an open question (V13). This class simply never offers a way to read or rewrite, so the
/// application is not the thing standing between the trail and an edit.
public sealed class JsonLinesAuditLog(string path) : IAuditLog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public void Append(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // UTF-8 without a BOM, and the newline written explicitly rather than by the framework:
        // a trail read on a Windows host and a Linux container has to be one format.
        string line = JsonSerializer.Serialize(Line.From(entry), Options) + "\n";

        // Opened per write and closed immediately. A long-lived handle on an append-only file
        // is a handle held across a failover, and the operation it is recording may be what
        // takes the process down.
        using FileStream stream = new(
            path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        stream.Write(Encoding.UTF8.GetBytes(line));
        stream.Flush(flushToDisk: true);
    }

    /// A flat shape with no type names and no nesting beyond one array, so a line stays
    /// readable by eye in a terminal at 3 a.m. as well as by a parser.
    private sealed record Line
    {
        public required string At { get; init; }

        public required string User { get; init; }

        public required string Host { get; init; }

        public required string Operation { get; init; }

        public required string Subject { get; init; }

        public required string Stage { get; init; }

        public required string Detail { get; init; }

        public required IReadOnlyList<string> Unverified { get; init; }

        public required string Version { get; init; }

        public static Line From(AuditEntry entry) => new()
        {
            // Round-trip format: an audit line compared across two hosts must not depend on
            // either host's locale or offset.
            At = entry.At.ToUniversalTime().ToString("O", null),
            User = entry.User,
            Host = entry.HostName,
            Operation = entry.Operation,
            Subject = entry.Subject,
            Stage = entry.Stage.ToString(),
            Detail = entry.Detail,
            Unverified = entry.Unverified,
            Version = entry.Version,
        };
    }
}
