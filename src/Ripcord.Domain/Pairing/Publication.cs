namespace Ripcord.Domain.Pairing;

public enum PublicationKind
{
    Published,

    /// Hyper-V could not be read: there was nothing to publish.
    NotRead,

    /// Read, and the file could not be written.
    NotWritten,

    /// `listener.enabled: false`: nobody would be served it.
    ListenerDisabled,
}

/// What one attempt to publish this host's snapshot came to. `Reason` says why it did not, in
/// one line; `Notes` are the degradations of a read that still published.
public sealed record Publication(
    PublicationKind Kind, string Path, DateTimeOffset At, string? Reason, IReadOnlyList<string> Notes)
{
    public bool Succeeded => this.Kind == PublicationKind.Published;

    public bool Equals(Publication? other) =>
        other is not null
        && this.Kind == other.Kind
        && this.Path == other.Path
        && this.At == other.At
        && this.Reason == other.Reason
        && Structural.Same(this.Notes, other.Notes);

    public override int GetHashCode() => HashCode.Combine(this.Kind, this.Path, this.At, this.Reason);
}
