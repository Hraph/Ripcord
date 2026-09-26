using Ripcord.Domain.Diagnostics;

namespace Ripcord.Domain.Deployment;

/// One of the Windows services Ripcord runs from its own binary: the verb the service manager
/// starts it with, the virtual account it runs as, and where it writes. Everything that used
/// to assume the listener is the only one reads it from here.
public sealed record RipcordService(
    string Name, string Verb, string Role, DiagnosticOrigin Origin, string ProcessFile)
{
    /// Network-facing and unprivileged (D18): it serves the snapshot, it never reads Hyper-V.
    public static RipcordService Listener { get; } =
        new("ripcord", "serve", "listener", DiagnosticOrigin.Listener, "listener-process.txt");

    /// Reads Hyper-V and rewrites the snapshot; no socket at all.
    public static RipcordService Publisher { get; } =
        new("ripcord-publish", "publish", "publisher", DiagnosticOrigin.Publisher, "publisher-process.txt");

    public static IReadOnlyList<RipcordService> All { get; } = [Listener, Publisher];

    public string Account => $@"NT SERVICE\{this.Name}";

    public string CommandLine(string binaryPath) => $"\"{binaryPath}\" {this.Verb}";

    /// The service a verb is run as, when the service manager started it; null for any other.
    public static RipcordService? ForVerb(string? verb) =>
        All.FirstOrDefault(service => string.Equals(service.Verb, verb, StringComparison.Ordinal));
}
