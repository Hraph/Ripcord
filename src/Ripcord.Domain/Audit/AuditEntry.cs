namespace Ripcord.Domain.Audit;

/// One line of the audit log (decision D8). Append-only, one entry per event, never re-read in
/// order to rewrite: immutability comes from an ACL set at install time, not from this code,
/// and that is stated here so nobody later mistakes the application for the guarantee.
///
/// `Unverified` is the field that earns this type its place in a mutating sequence. A failover
/// proceeds past unknowns on purpose — an unplanned one gates on nothing the report says,
/// because production is already down — and what it could not see has to be **durable before
/// the first mutation**. Console output during an incident is scrolled past and then lost; this
/// is what somebody reads the next morning working out why a VM is where it is. Afterwards the
/// host may not be in a state to write anything at all, which is why the ordering is part of
/// the record rather than a preference about logging.
public sealed record AuditEntry(
    DateTimeOffset At,
    string User,
    string HostName,
    string Operation,
    string Subject,
    AuditStage Stage,
    string Detail,
    IReadOnlyList<string> Unverified,
    string Version)
{
    public bool Equals(AuditEntry? other) =>
        other is not null
        && this.At == other.At
        && this.User == other.User
        && this.HostName == other.HostName
        && this.Operation == other.Operation
        && this.Subject == other.Subject
        && this.Stage == other.Stage
        && this.Detail == other.Detail
        && this.Version == other.Version
        && Structural.Same(this.Unverified, other.Unverified);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(this.At);
        hash.Add(this.User);
        hash.Add(this.HostName);
        hash.Add(this.Operation);
        hash.Add(this.Subject);
        hash.Add(this.Stage);
        hash.Add(this.Detail);
        hash.Add(this.Version);
        Structural.Add(ref hash, this.Unverified);
        return hash.ToHashCode();
    }
}

/// Which end of an operation the entry records. Both are written, and the first one is written
/// before anything changes — an operation that only logs its outcome logs nothing at all when
/// the host stops being writable part-way through, which is exactly when the record matters.
public enum AuditStage
{
    Starting,
    Finished,
}
