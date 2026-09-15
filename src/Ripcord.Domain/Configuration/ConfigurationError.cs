namespace Ripcord.Domain.Configuration;

/// Path is the dotted location in the file, so the operator can go straight to the line.
public sealed record ConfigurationError(
    string Path, string Message, ConfigurationErrorKind Kind = ConfigurationErrorKind.Invalid);

/// Whether the file is wrong or simply not there. Two different situations with two different
/// repairs — one is edited, the other is created — and they were saying the same sentence.
public enum ConfigurationErrorKind
{
    Invalid,
    Missing,
}

/// What to say to somebody who has a binary and no configuration. It names the path the
/// command was looking at and the one command that produces one, and nothing else: this is
/// read on a host where the next step is not obvious and the console is a KVM.
public static class MissingConfiguration
{
    public static bool In(IReadOnlyList<ConfigurationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return errors.Any(error => error.Kind == ConfigurationErrorKind.Missing);
    }

    public static IReadOnlyList<string> Lines(string path) =>
    [
        "ripcord: no configuration on this host.",
        $"  expected at {Printable.Of(path)}",
        "  create one with:  ripcord init",
    ];
}
