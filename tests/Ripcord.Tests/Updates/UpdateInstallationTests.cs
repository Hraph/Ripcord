using Ripcord.Application.Updates;
using Ripcord.Domain;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Updates;

namespace Ripcord.Tests.Updates;

/// What actually happens on the host, and — mostly — what does not. Every test here is about
/// the boundary between "nothing was changed" and "this host is in a state somebody has to
/// look at", because that boundary is what the exit code promises.
public sealed class UpdateInstallationTests
{
    private const string BinaryPath = @"D:\Ripcord\ripcord.exe";

    [Fact]
    public async Task A_verified_release_is_installed_in_the_planned_order()
    {
        RecordingSwap swap = new();

        UpdateResult result = await Install(swap, Source.Genuine());

        Assert.Equal(ExitCode.Success, result.Code);
        Assert.Equal(
            [
                UpdateAction.Download,
                UpdateAction.Verify,
                UpdateAction.DiscardPrevious,
                UpdateAction.SetAside,
                UpdateAction.Install,
            ],
            result.Applied.Select(step => step.Action));
    }

    /// The whole feature in one test. A release that does not verify never reaches the
    /// filesystem — not staged, not set aside, nothing.
    [Fact]
    public async Task A_release_that_does_not_verify_never_touches_the_host()
    {
        RecordingSwap swap = new();

        UpdateResult result = await Install(swap, Source.SignedByAStranger());

        Assert.NotEqual(ExitCode.Success, result.Code);
        Assert.Equal(ExitCode.Refused, result.Code);
        Assert.Empty(swap.Moves);
        Assert.Contains("signature", result.FailureMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_release_that_could_not_be_fetched_leaves_the_host_alone()
    {
        RecordingSwap swap = new();

        UpdateResult result = await Install(swap, Source.Unreachable());

        Assert.Equal(ExitCode.LocalAccessFailure, result.Code);
        Assert.Empty(swap.Moves);
    }

    /// Nothing had been applied, so the host is exactly where it was found. That is code 3,
    /// not 5 — audit finding 7 is the reason the two are told apart.
    [Fact]
    public async Task A_failure_before_anything_moved_reports_an_untouched_host()
    {
        RecordingSwap swap = new(failOn: UpdateAction.DiscardPrevious);

        UpdateResult result = await Install(swap, Source.Genuine());

        Assert.Equal(ExitCode.LocalAccessFailure, result.Code);
        Assert.Empty(swap.Moves);
        Assert.Null(result.Rollback);
    }

    /// The running binary has been moved aside and the new one is not in place. This is the
    /// one moment the host has no `ripcord.exe`, and it is exactly what the rollback is for.
    [Fact]
    public async Task A_failure_at_the_last_move_puts_the_old_binary_back()
    {
        RecordingSwap swap = new(failOn: UpdateAction.Install);

        UpdateResult result = await Install(swap, Source.Genuine());

        Assert.True(swap.Restored);
        Assert.NotNull(result.Rollback);
        Assert.True(result.Rollback!.Succeeded);
        Assert.Equal(ExitCode.Refused, result.Code);
    }

    /// The rollback is the thing that turns a failed swap back into "nothing was changed".
    /// When it fails too, the host genuinely is in an intermediate state and the exit code has
    /// to say so — it is the difference between re-running the command and going to look.
    [Fact]
    public async Task A_rollback_that_fails_leaves_an_intermediate_state()
    {
        RecordingSwap swap = new(failOn: UpdateAction.Install, restoreFails: true);

        UpdateResult result = await Install(swap, Source.Genuine());

        Assert.Equal(ExitCode.IntermediateState, result.Code);
        Assert.False(result.Rollback!.Succeeded);
        Assert.Contains("ripcord.exe", result.ManualRecovery!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_plan_that_changes_nothing_is_not_applied()
    {
        RecordingSwap swap = new();

        UpdateResult result = await Install(
            swap, Source.Genuine(), UpdatePlan.For(Subjects.UpToDate()));

        Assert.Equal(ExitCode.Success, result.Code);
        Assert.Empty(swap.Moves);
    }

    private static Task<UpdateResult> Install(
        IBinarySwap swap, IReleaseSource source, UpdatePlan? plan = null) =>
        new UpdateInstallation(source, swap, Keys.Pinned).ApplyAsync(
            plan ?? UpdatePlan.For(Subjects.Available()),
            BinaryPath,
            "0.2.0",
            CancellationToken.None);

    /// Records what it was asked to move, and can be told to fail at one of them.
    private sealed class RecordingSwap(
        UpdateAction? failOn = null, bool restoreFails = false) : IBinarySwap
    {
        public List<UpdateAction> Moves { get; } = [];

        public bool Restored { get; private set; }

        public StagedBinaries Observe(string binaryPath) => new(false, false);

        public void Apply(UpdateStep move, StagedRelease release, CancellationToken cancellationToken)
        {
            if (move.Action == failOn)
            {
                throw new IOException($"the host refused to {move.Action}");
            }

            this.Moves.Add(move.Action);
        }

        public void Restore(string binaryPath)
        {
            this.Restored = true;

            if (restoreFails)
            {
                throw new IOException("the binary could not be put back");
            }
        }

        public string? Swapped { get; private set; }

        

        public SwapOutcome SwapWithPrevious(string binaryPath)
        {
            this.Swapped = binaryPath;
            return SwapOutcome.Exchanged;
        }
    }
}
