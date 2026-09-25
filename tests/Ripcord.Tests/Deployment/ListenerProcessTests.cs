using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Deployment;

/// The build the running listener reports. After `ripcord update` the binary on disk is the new
/// one and the process is still the old one, so only the process can say.
public class ListenerProcessTests
{
    private const string Old = "0.6.0+abc1234";

    private const string New = "0.7.0+def5678";

    private const string Binary = @"C:\Program Files\Ripcord\ripcord.exe";

    private static readonly ObservedService Running = new(
        true, $"\"{Binary}\" serve", ServiceRunState.Running, "Auto", 0, 0,
        ProcessId: 4812);

    [Fact]
    public void What_the_listener_writes_is_read_back() =>
        Assert.Equal(
            new ListenerProcess(Old, 4812),
            ListenerProcess.Parse(new ListenerProcess(Old, 4812).Text().Split('\n')));

    /// A half-written or hand-edited file is no build at all, never a guessed one.
    [Theory]
    [InlineData("")]
    [InlineData("0.6.0+abc1234")]
    [InlineData("0.6.0+abc1234\nnot-a-pid")]
    [InlineData("0.6.0+abc1234\n0")]
    [InlineData("0.6.0+abc1234\n-12")]
    [InlineData("0.6.0+abc1234\n4812\nextra")]
    public void Anything_but_the_two_lines_is_not_a_record(string text) =>
        Assert.Null(ListenerProcess.Parse(text.Split('\n')));

    [Fact]
    public void A_service_that_is_not_running_has_no_build_to_report() =>
        Assert.Null(RunningBuild.Judge(
            Running with { State = ServiceRunState.Stopped }, new ListenerProcess(Old, 4812), New, Binary));

    [Fact]
    public void The_same_build_as_this_binary_is_current()
    {
        RunningBuild build = RunningBuild.Judge(Running, new ListenerProcess(New, 4812), New, Binary)!;

        Assert.Equal(New, build.Build);
        Assert.False(build.Outdated);
    }

    [Fact]
    public void The_old_build_still_running_after_an_update_is_outdated()
    {
        RunningBuild build = RunningBuild.Judge(Running, new ListenerProcess(Old, 4812), New, Binary)!;

        Assert.Equal(Old, build.Build);
        Assert.True(build.Outdated);
    }

    /// A record left by an earlier run names a build that is no longer running.
    [Fact]
    public void A_record_from_another_process_is_not_believed()
    {
        RunningBuild build = RunningBuild.Judge(Running, new ListenerProcess(New, 1111), New, Binary)!;

        Assert.Null(build.Build);
        Assert.False(build.Outdated);
        Assert.Equal("not recorded by the process running now", build.Unknown);
    }

    /// Restarting the service cannot bring another copy of the binary to this build.
    [Fact]
    public void A_service_running_another_copy_is_not_outdated()
    {
        RunningBuild build = RunningBuild.Judge(
            Running, new ListenerProcess(Old, 4812), New, @"C:\Temp\ripcord.exe")!;

        Assert.Equal(Old, build.Build);
        Assert.False(build.Outdated);
    }

    [Fact]
    public void No_record_is_unknown_rather_than_current()
    {
        RunningBuild build = RunningBuild.Judge(Running, null, New, Binary)!;

        Assert.Null(build.Build);
        Assert.NotNull(build.Unknown);
    }

    [Fact]
    public void No_process_id_from_windows_is_unknown()
    {
        RunningBuild build = RunningBuild.Judge(
            Running with { ProcessId = null }, new ListenerProcess(New, 4812), New, Binary)!;

        Assert.Null(build.Build);
        Assert.Equal("Windows did not give its process id", build.Unknown);
    }

    [Fact]
    public void An_outdated_listener_is_told_to_restart()
    {
        ServiceVerdict verdict = ServiceDiagnosis.Diagnose(
            Running, logsWritable: true, log: null,
            build: RunningBuild.Judge(Running, new ListenerProcess(Old, 4812), New, Binary));

        Assert.Contains(Old, verdict.Why, StringComparison.Ordinal);
        Assert.Equal(["ripcord service restart"], verdict.Next);
    }

    [Fact]
    public void A_current_or_unknown_build_says_nothing() =>
        Assert.All(
            new[]
            {
                RunningBuild.Judge(Running, new ListenerProcess(New, 4812), New, Binary),
                RunningBuild.Judge(Running, null, New, Binary),
            },
            build => Assert.Equal(
                ServiceVerdict.None,
                ServiceDiagnosis.Diagnose(Running, logsWritable: true, log: null, build: build)));
}
