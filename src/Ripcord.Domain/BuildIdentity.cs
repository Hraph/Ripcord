namespace Ripcord.Domain;

/// Which binary a host is running. Both parts are needed: two builds at the same version from
/// different commits differ in exactly the way nobody thinks to check.
///
/// `Unknown` is what a build made outside a repository reports, and it is carried rather than
/// rejected — a host that cannot say what it is running is a fact, not an error.
public sealed record BuildIdentity(string Version, string CommitHash)
{
    public const string Unknown = "unknown";

    public bool IsIdentified =>
        !string.Equals(this.Version, Unknown, StringComparison.OrdinalIgnoreCase)
        && !string.Equals(this.CommitHash, Unknown, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{this.Version}+{this.CommitHash}";
}
