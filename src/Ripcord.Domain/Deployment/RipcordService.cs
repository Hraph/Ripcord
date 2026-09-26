using Ripcord.Domain.Diagnostics;
using Ripcord.Domain.Pairing;

namespace Ripcord.Domain.Deployment;

/// One of the Windows services Ripcord runs from its own binary: the verb the service manager
/// starts it with, the virtual account it runs as, and where it writes. Everything that used
/// to assume the listener is the only one reads it from here.
public sealed record RipcordService(
    string Name,
    string Verb,
    string Role,
    DiagnosticOrigin Origin,
    string ProcessFile,

    /// What services.msc shows, for whoever finds it there without knowing what Ripcord is.
    string DisplayName,
    string Description)
{
    /// Network-facing and unprivileged (D18): it serves the snapshot, it never reads Hyper-V.
    public static RipcordService Listener { get; } =
        new(
            "ripcord",
            "serve",
            "listener",
            DiagnosticOrigin.Listener,
            "listener-process.txt",
            "Ripcord listener",
            "Serves this host's Hyper-V Replica snapshot to its Ripcord peer over TLS. "
                + "Stopped, the peer can no longer see this host.");

    /// Reads Hyper-V and rewrites the snapshot; no socket at all.
    public static RipcordService Publisher { get; } =
        new(
            "ripcord-publish",
            "publish",
            "publisher",
            DiagnosticOrigin.Publisher,
            "publisher-process.txt",
            "Ripcord publisher",
            $"Reads Hyper-V Replica state every {HostSnapshot.RepublishEvery.TotalSeconds:0} s and rewrites the snapshot the Ripcord "
                + "listener serves. Stopped, the peer reads a snapshot that ages.");

    public static IReadOnlyList<RipcordService> All { get; } = [Listener, Publisher];

    public string Account => $@"NT SERVICE\{this.Name}";

    public string CommandLine(string binaryPath) => $"\"{binaryPath}\" {this.Verb}";

    /// Where the process records its build, newest layout first: up to 0.9.0 the listener
    /// recorded it in `logs` itself, and a host just updated still runs that build.
    public IEnumerable<string> ProcessRecords(string logsFolder)
    {
        yield return WindowsPath.Join(logsFolder, this.ProcessFile);

        if (this.Origin == DiagnosticOrigin.Listener)
        {
            yield return WindowsPath.Join(WindowsPath.FolderOf(logsFolder), this.ProcessFile);
        }
    }

    /// Where the service writes when it runs `binaryPath`.
    public string LogsFolderBeside(string binaryPath) =>
        LogFolder.For(this.Origin, LogFolder.Beside(binaryPath));

    /// The service a verb is run as, when the service manager started it; null for any other.
    public static RipcordService? ForVerb(string? verb) =>
        All.FirstOrDefault(service => string.Equals(service.Verb, verb, StringComparison.Ordinal));
}
