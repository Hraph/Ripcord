namespace Ripcord.Application.TestFailover;

/// Whether the undo ran. Carried rather than thrown: the caller is already reporting a
/// failure when it reaches here, and a cleanup failure has to appear beside it rather than
/// replace it.
public sealed record Compensation(bool Succeeded, string? FailureMessage)
{
    public static readonly Compensation Done = new(true, null);

    public static Compensation Failed(string message) => new(false, message);

    /// Runs an action that must happen whatever else went wrong — and, above all, whatever
    /// the caller's cancellation token says. A `finally` awaiting on that token is a cleanup
    /// that never runs: by the time the `finally` is reached the token is already cancelled,
    /// so the compensating call is cancelled before it starts. Ctrl+C is exactly the case,
    /// and it is the one an operator produces deliberately when something looks wrong.
    ///
    /// The action gets a fresh token with its own deadline, so a cleanup that hangs cannot
    /// stop the report from being printed.
    ///
    /// This is the mechanism and not the policy. What to do when it fails belongs to the
    /// caller: milestone 3 is discarding a copy, and a bounded attempt followed by a loud
    /// report is the right answer there. A caller undoing a half-applied change to production
    /// would want something louder, and it is free to ask for it.
    public static async Task<Compensation> RunAsync(
        Func<CancellationToken, Task> action, TimeSpan deadline)
    {
        ArgumentNullException.ThrowIfNull(action);

        using CancellationTokenSource own = new(deadline);

        try
        {
            await action(own.Token).ConfigureAwait(false);
            return Done;
        }
        catch (OperationCanceledException)
        {
            return Failed($"did not finish within {(int)deadline.TotalSeconds}s");
        }
        catch (Exception exception)
        {
            return Failed(exception.Message);
        }
    }
}
