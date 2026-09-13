using Ripcord.Application.TestFailover;

namespace Ripcord.Tests.TestFailover;

/// The cleanup guarantee, on its own. A test failover that created a test VM must destroy it
/// even when the run was interrupted — and Ctrl+C is the interruption that matters, because
/// it is the one an operator produces on purpose when something looks wrong.
///
/// The trap this exists to avoid: a plain `finally` awaiting on the caller's token is a
/// cleanup that does not run. By the time the `finally` is reached the token is already
/// cancelled, so the compensating call is cancelled before it starts.
public class CompensationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_compensating_action_runs_and_reports_that_it_ran()
    {
        bool ran = false;

        Compensation result = await Compensation.RunAsync(
            _ => { ran = true; return Task.CompletedTask; }, Deadline);

        Assert.True(ran);
        Assert.True(result.Succeeded);
        Assert.Null(result.FailureMessage);
    }

    /// The two tests that used to sit here created a cancelled token and never passed it to
    /// `RunAsync` — because there is nowhere to pass it. They read as proof of the guarantee
    /// and proved nothing, which is the failure this whole milestone has been hunting, one
    /// level up.
    ///
    /// The guarantee cannot honestly be tested here. What can be tested is the property that
    /// makes it hold: the signature takes no caller token, so there is none in scope to
    /// thread through by mistake. That catches a later refactor "helpfully" adding one back.
    /// The behaviour itself is pinned at the call site, in TestFailoverSequenceTests.
    [Fact]
    public void The_signature_admits_no_caller_token()
    {
        Assert.DoesNotContain(
            typeof(Compensation)
                .GetMethod(nameof(Compensation.RunAsync))!
                .GetParameters(),
            parameter => parameter.ParameterType == typeof(CancellationToken));
    }

    /// A cleanup that fails is reported, never swallowed: an orphaned test VM holds disk on
    /// the target and breaks the next test, so silence here is the expensive answer.
    [Fact]
    public async Task A_failing_action_is_captured_rather_than_thrown()
    {
        Compensation result = await Compensation.RunAsync(
            _ => throw new InvalidOperationException("the VM is locked"), Deadline);

        Assert.False(result.Succeeded);
        Assert.Contains("the VM is locked", result.FailureMessage);
    }

    /// A cleanup that hangs must not hang the report. The deadline is the compensating
    /// action's own, and expiring it is a failure like any other.
    [Fact]
    public async Task An_action_that_outlives_its_deadline_fails_rather_than_hangs()
    {
        Compensation result = await Compensation.RunAsync(
            token => Task.Delay(Timeout.Infinite, token), TimeSpan.FromMilliseconds(50));

        Assert.False(result.Succeeded);
        Assert.Contains("did not finish", result.FailureMessage);
    }
}
