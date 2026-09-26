using Ripcord.Domain.Diagnostics;

namespace Ripcord.Domain.Deployment;

/// What the last run of a service wrote about how it ended.
public sealed record ServiceLogSummary(bool SawStart, ExitCode? Exit, bool Crashed, bool Cancelled)
{
    public static ServiceLogSummary Empty { get; } = new(false, null, false, false);

    /// The log says how the run ended, one way or another.
    public bool Said => this.Exit is not null || this.Crashed || this.Cancelled;

    /// A start and nothing after it about an end: the process went before it could say.
    public bool EndedSilently => this.SawStart && this.Exit is null && !this.Crashed && !this.Cancelled;
}

/// A service's own log, as `ripcord service` reads it back.
public static class ServiceLog
{
    /// Lines shown to the operator. More are read, to find the start of the last run.
    public const int ShownLines = 20;

    public const int ReadLines = 400;

    /// Today's file, then yesterday's: a service that stopped at 23:59 UTC left its reason in
    /// a file that is no longer today's a minute later.
    public static IReadOnlyList<string> Candidates(
        RipcordService service, string logsFolder, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(service);

        DiagnosticOrigin origin = service.Origin;

        return
        [
            WindowsPath.Join(logsFolder, LogFolder.FileName(origin, now)),
            WindowsPath.Join(logsFolder, LogFolder.FileName(origin, now.AddDays(-1))),
        ];
    }

    /// Only what follows the last start banner counts: an exit logged by an earlier run says
    /// nothing about why the current one stopped. With no banner in sight, the whole tail is
    /// the last run.
    public static ServiceLogSummary Summarise(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        List<(string Operation, string Message)> entries =
            [.. lines.Select(Entry).OfType<(string, string)>()];

        int banner = entries.FindLastIndex(
            entry => ServiceStartup.IsBanner(entry.Operation, entry.Message));

        ExitCode? exit = null;
        bool crashed = false;
        bool cancelled = false;

        foreach ((_, string message) in entries.Skip(banner + 1))
        {
            exit = CommandEntries.ExitIn(message) ?? exit;
            crashed |= CommandEntries.IsCrash(message);
            cancelled |= CommandEntries.IsCancelled(message);
        }

        return new ServiceLogSummary(banner >= 0, exit, crashed, cancelled);
    }

    /// `2026-09-25 14:00:00.000Z serve: exit 2: InvalidConfiguration`. Detail lines are
    /// indented and belong to the entry above; they are skipped.
    private static (string Operation, string Message)? Entry(string line)
    {
        if (line.Length == 0 || line[0] == ' ')
        {
            return null;
        }

        int stamp = line.IndexOf("Z ", StringComparison.Ordinal);

        if (stamp < 0)
        {
            return null;
        }

        string rest = line[(stamp + 2)..];
        int colon = rest.IndexOf(": ", StringComparison.Ordinal);

        return colon <= 0 ? null : (rest[..colon], rest[(colon + 2)..]);
    }
}
