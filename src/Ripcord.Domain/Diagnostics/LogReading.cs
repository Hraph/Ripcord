namespace Ripcord.Domain.Diagnostics;

/// The end of one log file, or why it could not be read.
public sealed record LogReading(string Path, IReadOnlyList<string> Lines, string? Unreadable)
{
    public bool Equals(LogReading? other) =>
        other is not null
        && this.Path == other.Path
        && this.Unreadable == other.Unreadable
        && Structural.Same(this.Lines, other.Lines);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(this.Path);
        hash.Add(this.Unreadable);
        Structural.Add(ref hash, this.Lines);
        return hash.ToHashCode();
    }
}
