using System.Globalization;

namespace Ripcord.Domain.Deployment;

/// What the listener writes about itself when it starts: its build and its process id.
///
/// The build of the running process, not of the file on disk: after `ripcord update` the two
/// differ until the service restarts, and the file on disk is the one that would mislead.
public sealed record ListenerProcess(string Build, int ProcessId)
{
    /// Beside the listener's logs, the only folder the service account may write to.
    public const string FileName = "listener-process.txt";

    public string Text() =>
        string.Create(CultureInfo.InvariantCulture, $"{this.Build}\n{this.ProcessId}\n");

    /// Null for anything but the two lines `Text` writes: a half-written file is not a build.
    public static ListenerProcess? Parse(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        string[] content = [.. lines.Select(line => line.Trim()).Where(line => line.Length > 0)];

        return content.Length == 2
            && int.TryParse(content[1], NumberStyles.None, CultureInfo.InvariantCulture, out int id)
            && id > 0
            ? new ListenerProcess(content[0], id)
            : null;
    }
}

/// The build the running listener reports, or why it cannot be told.
public sealed record RunningBuild(string? Build, string? Unknown, bool Outdated)
{
    /// Only for a running service, and only from a record whose process id is the one Windows
    /// gives: a record left by an earlier run names a build that is no longer running.
    public static RunningBuild? Judge(ObservedService service, ListenerProcess? recorded, string thisBuild)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (service.State != ServiceRunState.Running)
        {
            return null;
        }

        if (recorded is null)
        {
            return new RunningBuild(null, "not recorded: this listener predates the record", false);
        }

        if (service.ProcessId is not { } running)
        {
            return new RunningBuild(null, "Windows did not give its process id", false);
        }

        if (running != recorded.ProcessId)
        {
            return new RunningBuild(null, "not recorded by the process running now", false);
        }

        return new RunningBuild(
            recorded.Build, null, !string.Equals(recorded.Build, thisBuild, StringComparison.Ordinal));
    }
}
