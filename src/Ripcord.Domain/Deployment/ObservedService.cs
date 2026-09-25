namespace Ripcord.Domain.Deployment;

public enum ServiceRunState
{
    Running,
    Stopped,
    StartPending,
    StopPending,
    Paused,

    /// Windows did not say. Never read as stopped: that would send an operator to restart a
    /// listener that may be serving.
    Unknown,
}

/// The Windows service as Windows reports it, raw. Read without the configuration, so a
/// `ripcord.yaml` that does not load still leaves the service visible.
public sealed record ObservedService(
    bool Installed,

    /// What Windows runs, as registered: `"<binary>" serve`.
    string? CommandLine,
    ServiceRunState State,
    string? StartMode,
    int? Win32ExitCode,
    int? ServiceSpecificExitCode,

    /// Why the state could not be read, when it could not.
    string? Unreadable = null)
{
    public static ObservedService Absent { get; } =
        new(false, null, ServiceRunState.Stopped, null, null, null);

    public string? BinaryPath => BinaryIn(this.CommandLine);

    public bool IsDisabled =>
        string.Equals(this.StartMode, "Disabled", StringComparison.OrdinalIgnoreCase);

    public ServiceExit? LastExit =>
        this.Win32ExitCode is { } win32
            ? ServiceExitCode.FromWindows(win32, this.ServiceSpecificExitCode)
            : null;

    /// The command line is `"<path>" serve`, or an unquoted path with no space in it.
    public static string? BinaryIn(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        string trimmed = commandLine.Trim();

        return trimmed.StartsWith('"') && trimmed.IndexOf('"', 1) is int end and > 0
            ? trimmed[1..end]
            : trimmed.Split(' ')[0];
    }

    /// `Win32_Service.State` is an invariant English string whatever the host's language.
    public static ServiceRunState ParseState(string? state) => state switch
    {
        "Running" => ServiceRunState.Running,
        "Stopped" => ServiceRunState.Stopped,
        "Start Pending" or "Continue Pending" => ServiceRunState.StartPending,
        "Stop Pending" or "Pause Pending" => ServiceRunState.StopPending,
        "Paused" => ServiceRunState.Paused,
        _ => ServiceRunState.Unknown,
    };
}
