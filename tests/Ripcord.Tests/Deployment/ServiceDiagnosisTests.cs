using Ripcord.Domain;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Deployment;

/// Why a stopped listener stopped, from Windows' record and the listener's own log.
public class ServiceDiagnosisTests
{
    private const string LogPath = @"C:\Program Files\Ripcord\logs\listener-2026-09-25.log";

    private static readonly ObservedService Stopped = new(
        true,
        "\"C:\\Program Files\\Ripcord\\ripcord.exe\" serve",
        ServiceRunState.Stopped,
        "Auto",
        0,
        0);

    private static readonly DateTimeOffset At = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);

    private static string Line(string operation, string message) =>
        DiagnosticEntry.Of(operation, message).Render(At)[0];

    private static string Banner() =>
        ServiceStartup.Banner("0.4.1", @"C:\Program Files\Ripcord\ripcord.yaml").Render(At)[0];

    private static string Exit(ExitCode code) => CommandEntries.Exited("serve", code).Render(At)[0];

    private static LogReading Log(params string[] lines) => new(LogPath, lines, null);

    [Fact]
    public void Not_installed_names_the_install_dry_run()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(ObservedService.Absent, null, null);

        Assert.Equal("it is not installed", verdict.Why);
        Assert.Equal(["ripcord service install --dry-run"], verdict.Next);
    }

    [Fact]
    public void Running_has_no_verdict() =>
        Assert.Equal(
            ServiceVerdict.None,
            ServiceDiagnosis.Diagnose(
                Stopped with { State = ServiceRunState.Running }, true, null));

    [Fact]
    public void Unknown_state_is_never_read_as_stopped()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { State = ServiceRunState.Unknown, Unreadable = "RPC unavailable" },
            false,
            null);

        Assert.Equal("Windows did not say whether it runs: RPC unavailable", verdict.Why);
        Assert.Empty(verdict.Next);
    }

    [Fact]
    public void Disabled_start_mode_is_said_before_anything_else()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { StartMode = "Disabled" },
            true,
            Log(Banner(), Exit(ExitCode.InvalidConfiguration)));

        Assert.Contains("Disabled", verdict.Why, StringComparison.Ordinal);
        Assert.Contains("sc.exe config ripcord start= auto", verdict.Next);
    }

    [Fact]
    public void Logged_exit_after_the_last_banner_is_believed_when_windows_agrees()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = ServiceExitCode.ToWindows(ExitCode.InvalidConfiguration) },
            true,
            Log(Banner(), Line("serve", "listening"), Exit(ExitCode.InvalidConfiguration)));

        Assert.Equal("it stopped with exit 2: the configuration did not load", verdict.Why);
        Assert.Equal(["ripcord check"], verdict.Next);
    }

    [Fact]
    public void Logged_exit_is_believed_when_windows_recorded_a_clean_stop()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped, true, Log(Banner(), Exit(ExitCode.InvalidConfiguration)));

        Assert.Equal("it stopped with exit 2: the configuration did not load", verdict.Why);
    }

    [Fact]
    public void A_stale_exit_in_the_log_gives_way_to_a_different_ripcord_code_from_windows()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = ServiceExitCode.ToWindows(ExitCode.LocalAccessFailure) },
            true,
            Log(Banner(), Exit(ExitCode.InvalidConfiguration)));

        Assert.Equal(
            "Windows recorded exit 3: it could not reach a local resource: log folder, "
                + "certificate or port; its log is from an earlier run",
            verdict.Why);
        Assert.Equal(["ripcord check", "ripcord service install --dry-run"], verdict.Next);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_clean_log_never_hides_a_failed_start_windows_recorded(bool cancelled)
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = ServiceExitCode.ToWindows(ExitCode.LocalAccessFailure) },
            true,
            Log(
                Banner(),
                cancelled ? CommandEntries.Cancelled("serve").Render(At)[0] : Exit(ExitCode.Success)));

        Assert.StartsWith("Windows recorded exit 3", verdict.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void A_clean_log_and_logs_not_writable_blames_the_logs_folder()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = ServiceExitCode.ToWindows(ExitCode.LocalAccessFailure) },
            false,
            Log(Banner(), Exit(ExitCode.Success)));

        Assert.Contains("cannot write its logs folder", verdict.Why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1053, "Windows recorded 1053: it did not answer the start request in time")]
    [InlineData(1069, "Windows recorded 1069: the service account could not log on")]
    [InlineData(2, "Windows recorded 2: the binary was not found")]
    public void A_clean_log_never_hides_a_code_windows_set_after_it(int win32, string expected)
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = win32 }, true, Log(Banner(), Exit(ExitCode.Success)));

        Assert.Equal(expected + "; its log is from an earlier run", verdict.Why);
        Assert.Equal(ServiceDiagnosis.EventLogCommands, verdict.Next);
    }

    [Fact]
    public void A_windows_code_with_no_log_does_not_mention_an_earlier_run()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = 1053 }, true, null);

        Assert.Equal("Windows recorded 1053: it did not answer the start request in time", verdict.Why);
    }

    [Fact]
    public void Aborted_after_a_logged_exit_is_the_process_dying_after_reporting()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = ServiceExitCode.Aborted },
            true,
            Log(Banner(), Exit(ExitCode.InvalidConfiguration)));

        Assert.Equal("it stopped with exit 2: the configuration did not load", verdict.Why);
    }

    [Fact]
    public void A_crash_logged_agrees_with_the_exit_3_the_host_sets()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = ServiceExitCode.ToWindows(ExitCode.LocalAccessFailure) },
            true,
            Log(Banner(), CommandEntries.Crashed("serve", "boom").Render(At)[0]));

        Assert.Equal("it stopped on an unhandled error, written to its log", verdict.Why);
    }

    [Fact]
    public void Never_started_says_so_and_names_the_restart()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = ServiceDiagnosis.NeverStarted },
            true,
            Log(Banner(), Exit(ExitCode.Success)));

        Assert.Equal("it has not been started since Windows booted", verdict.Why);
        Assert.Equal(["ripcord service restart"], verdict.Next);
    }

    [Fact]
    public void A_disabled_listener_is_said_before_the_clean_stop_and_points_to_remove()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped, true, Log(Banner(), Exit(ExitCode.Success)), listenerDisabled: true);

        Assert.Equal(
            "the listener is disabled in ripcord.yaml (listener.enabled: false)", verdict.Why);
        Assert.Equal(["ripcord service remove"], verdict.Next);
    }

    [Fact]
    public void Access_denied_asks_for_an_elevated_console()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { State = ServiceRunState.Unknown, Unreadable = ObservedService.AccessDenied },
            null,
            null);

        Assert.Contains("elevated console", verdict.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exit_before_the_last_banner_is_ignored()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped,
            true,
            Log(Banner(), Exit(ExitCode.InvalidConfiguration), Banner(), Line("serve", "listening")));

        Assert.Equal("it started, then stopped without writing why", verdict.Why);
        Assert.Equal(ServiceDiagnosis.EventLogCommands, verdict.Next);
    }

    [Fact]
    public void Windows_code_is_read_when_the_log_says_nothing()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = ServiceExitCode.ToWindows(ExitCode.InvalidConfiguration) },
            true,
            null);

        Assert.Equal("Windows recorded exit 2: the configuration did not load", verdict.Why);
    }

    [Fact]
    public void A_service_specific_error_carries_ripcords_code()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = 1066, ServiceSpecificExitCode = 2 }, true, null);

        Assert.Equal("Windows recorded exit 2: the configuration did not load", verdict.Why);
    }

    [Fact]
    public void Crash_line_says_the_error_is_in_the_log()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = ServiceExitCode.ToWindows(ExitCode.LocalAccessFailure) },
            true,
            Log(Banner(), CommandEntries.Crashed("serve", "boom").Render(At)[0], "    boom"));

        Assert.Equal("it stopped on an unhandled error, written to its log", verdict.Why);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_clean_stop_or_a_cancel_reads_as_an_operator_stop(bool cancelled)
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped,
            true,
            Log(
                Banner(),
                cancelled
                    ? CommandEntries.Cancelled("serve").Render(At)[0]
                    : Exit(ExitCode.Success)));

        Assert.StartsWith("it stopped cleanly", verdict.Why, StringComparison.Ordinal);
        Assert.Equal(["ripcord service restart"], verdict.Next);
    }

    [Fact]
    public void No_log_and_logs_not_writable_blames_the_logs_folder()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = ServiceExitCode.ToWindows(ExitCode.LocalAccessFailure) },
            false,
            null);

        Assert.Contains("cannot write its logs folder", verdict.Why, StringComparison.Ordinal);
        Assert.Equal(["ripcord service install --dry-run"], verdict.Next);
    }

    [Fact]
    public void No_log_at_all_says_it_died_before_logging_and_prints_the_event_log_commands()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(Stopped, true, null);

        Assert.StartsWith("no log today or yesterday", verdict.Why, StringComparison.Ordinal);
        Assert.Equal(ServiceDiagnosis.EventLogCommands, verdict.Next);
    }

    [Fact]
    public void Process_aborted_points_to_the_event_log()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped with { Win32ExitCode = ServiceExitCode.Aborted }, true, Log(Banner()));

        Assert.Equal("the process ended without reporting to Windows", verdict.Why);
        Assert.Equal(ServiceDiagnosis.EventLogCommands, verdict.Next);
    }

    [Fact]
    public void An_unreadable_log_says_so()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Stopped, true, new LogReading(LogPath, [], "Access is denied."));

        Assert.Equal("its log could not be read: Access is denied.", verdict.Why);
    }

    [Fact]
    public void Every_exit_code_has_its_own_meaning()
    {
        ExitCode[] codes = Enum.GetValues<ExitCode>();

        Assert.Equal(6, codes.Length);
        Assert.Equal(codes.Length, codes.Select(ServiceDiagnosis.Meaning).Distinct().Count());
        Assert.All(codes, code => Assert.DoesNotContain("exit", ServiceDiagnosis.Meaning(code), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, "0, a normal stop")]
    [InlineData(0x20000002, "0x20000002, Ripcord exit 2: the configuration did not load")]
    [InlineData(1067, "1067, the process ended without reporting to Windows")]
    [InlineData(2, "2: the binary was not found")]
    [InlineData(1053, "1053: it did not answer the start request in time")]
    [InlineData(1077, "1077: it has not been started since Windows booted")]
    [InlineData(1234, "1234, a Windows code, not Ripcord's")]
    public void Windows_codes_are_printed_as_sc_prints_them_with_their_meaning(
        int win32, string expected) =>
        Assert.Equal(expected, ServiceDiagnosis.WindowsMeaning(ServiceExitCode.FromWindows(win32)));

    /// Typed from the screen under a four-column indent.
    [Fact]
    public void Event_log_commands_fit_the_console()
    {
        Assert.Equal(2, ServiceDiagnosis.EventLogCommands.Count);
        Assert.All(ServiceDiagnosis.EventLogCommands, line => Assert.True(line.Length <= 71, line));
    }

    [Fact]
    public void Event_log_command_names_the_source_the_listener_writes_under() =>
        Assert.Contains(
            $"-ProviderName {DeploymentPlan.EventSource} ",
            ServiceDiagnosis.EventLogCommands[0],
            StringComparison.Ordinal);

    [Fact]
    public void A_start_that_stopped_at_once_is_a_failure_with_windows_code()
    {
        string? failure = ServiceDiagnosis.StartFailure(
            Stopped with { Win32ExitCode = ServiceExitCode.ToWindows(ExitCode.InvalidConfiguration) });

        Assert.Equal(
            "the service stopped right after it started: 0x20000002, Ripcord exit 2: the "
                + "configuration did not load",
            failure);
    }

    [Theory]
    [InlineData(ServiceRunState.Running)]
    [InlineData(ServiceRunState.StartPending)]
    [InlineData(ServiceRunState.Unknown)]
    public void A_start_that_runs_starts_or_cannot_be_read_is_not_a_failure(ServiceRunState state) =>
        Assert.Null(ServiceDiagnosis.StartFailure(Stopped with { State = state }));

    [Fact]
    public void A_service_that_is_not_there_is_not_judged() =>
        Assert.Null(ServiceDiagnosis.StartFailure(ObservedService.Absent));
}
