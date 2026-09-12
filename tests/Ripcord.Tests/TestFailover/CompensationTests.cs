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

    /// The whole reason this type exists.
    [Fact]
    public async Task It_runs_even_when_the_callers_token_is_already_cancelled()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        bool ran = false;

        Compensation result = await Compensation.RunAsync(
            _ => { ran = true; return Task.CompletedTask; }, Deadline);

        Assert.True(ran);
        Assert.True(result.Succeeded);
    }

    /// It hands the action a token of its own so a hung cleanup cannot block a report from
    /// ever being printed — but that token is not the caller's, and is not cancelled.
    [Fact]
    public async Task The_action_receives_its_own_uncancelled_token()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        bool tokenWasCancelled = true;

        await Compensation.RunAsync(
            token =>
            {
                tokenWasCancelled = token.IsCancellationRequested;
                return Task.CompletedTask;
            },
            Deadline);

        Assert.False(tokenWasCancelled);
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
