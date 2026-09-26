using Ripcord.Application.TestFailover;
using Ripcord.Domain;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Updates;

namespace Ripcord.Application.Updates;

/// What the update did, and how far it got. `Applied` is what completed; the exit code answers the
/// only question that matters afterwards — is this host where it was, or does somebody have
/// to go and look.
public sealed record UpdateResult(
    ExitCode Code,
    IReadOnlyList<UpdateStep> Applied,
    UpdateStep? Failed,
    string? FailureMessage,
    Compensation? Rollback,
    string? ManualRecovery);

/// Carries out an `UpdatePlan`. It decides nothing about *whether* to update — that was
/// settled before the operator typed the node name — and it decides nothing about whether the
/// release is genuine either: `ReleaseSignature` answers that, in the Domain.
///
/// A host carrying no key at all refuses every install rather than trusting one — the
/// `pinnedPublicKey` is nullable for exactly that case, and `ReleaseSignature` answers it.
///
/// The order is the safety property. Fetching and verifying change nothing on the host, so
/// everything that can refuse has refused before the first irreversible move; and the first
/// irreversible move, setting the running binary aside, is the one the rollback exists for.
///
/// `starting` hears each step before it runs and `downloading` the binary arriving, so a
/// download that takes minutes is not silence.
public sealed class UpdateInstallation(
    IReleaseSource source,
    IBinarySwap swap,
    string? pinnedPublicKey,
    Action<UpdateStep>? starting = null,
    Action<DownloadedBytes>? downloading = null)
{
    /// Long enough for a rename on a busy volume, short enough that a wedged filesystem is
    /// reported rather than waited on for ever.
    private static readonly TimeSpan RollbackDeadline = TimeSpan.FromSeconds(30);

    public async Task<UpdateResult> ApplyAsync(
        UpdatePlan plan,
        string binaryPath,
        string version,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!plan.ChangesAnything)
        {
            return new UpdateResult(ExitCode.Success, [], null, null, null, null);
        }

        List<UpdateStep> applied = [];
        UpdateStep download = Step(plan, UpdateAction.Download);
        UpdateStep verify = Step(plan, UpdateAction.Verify);

        this.Announce(download);

        FetchedRelease fetched = await source
            .FetchAsync(version, this.Report, cancellationToken)
            .ConfigureAwait(false);

        if (!fetched.Arrived)
        {
            return Untouched(plan, UpdateAction.Download, fetched.FailureMessage!, applied);
        }

        applied.Add(download);
        this.Announce(verify);

        // Before anything is moved. A release that cannot be shown to be genuine is refused,
        // and refusing is the feature working rather than a degraded mode.
        SignatureVerdict verdict = ReleaseSignature.Verify(
            fetched.Payload, fetched.Signature, pinnedPublicKey);

        // Exit 4 rather than 3, though nothing was applied either way: this is a precondition
        // refusing, not a host that could not be read. The operator's next move differs —
        // a failed read is worth retrying, a failed signature is worth investigating.
        if (!verdict.IsGenuine)
        {
            return new UpdateResult(
                ExitCode.Refused,
                applied,
                verify,
                $"this release was not installed: {verdict.Explanation}",
                null,
                null);
        }

        applied.Add(verify);

        return await this.SwapAsync(plan, binaryPath, version, fetched.Payload!, applied, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<UpdateResult> SwapAsync(
        UpdatePlan plan,
        string binaryPath,
        string version,
        byte[] payload,
        List<UpdateStep> applied,
        CancellationToken cancellationToken)
    {
        StagedRelease release = new(binaryPath, payload, version);
        bool setAside = false;

        foreach (UpdateStep move in plan.Steps.Where(OnDisk))
        {
            this.Announce(move);

            try
            {
                swap.Apply(move, release, cancellationToken);
                applied.Add(move);
                setAside |= move.Action == UpdateAction.SetAside;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return setAside
                    ? await this.RolledBackAsync(binaryPath, applied, move, exception).ConfigureAwait(false)
                    : Untouched(plan, move.Action, exception.Message, applied);
            }
        }

        return new UpdateResult(ExitCode.Success, applied, null, null, null, null);
    }

    /// The running binary is aside and the new one is not in place, so this host currently has
    /// no `ripcord.exe`. Putting the old one back turns that into "nothing was changed"; when
    /// it cannot, the exit code stops saying so and the operator is handed the two commands.
    private async Task<UpdateResult> RolledBackAsync(
        string binaryPath, List<UpdateStep> applied, UpdateStep failed, Exception failure)
    {
        Compensation rollback = await Compensation
            .RunAsync(token => Task.Run(() => swap.Restore(binaryPath), token), RollbackDeadline)
            .ConfigureAwait(false);

        return rollback.Succeeded
            ? new UpdateResult(
                ExitCode.Refused,
                applied,
                failed,
                failure.Message,
                rollback,
                null)
            : new UpdateResult(
                ExitCode.IntermediateState,
                applied,
                failed,
                failure.Message,
                rollback,
                $"this host has no ripcord.exe: rename ripcord.exe.old back to ripcord.exe "
                + $"in {Directory(binaryPath)}");
    }

    /// Progress is shown, never obeyed: a console that cannot be written to must not stop the
    /// swap between setting the binary aside and putting the new one in place.
    private void Announce(UpdateStep step) => Quietly(() => starting?.Invoke(step));

    private void Report(DownloadedBytes bytes) => Quietly(() => downloading?.Invoke(bytes));

    private static void Quietly(Action report)
    {
        try
        {
            report();
        }
        catch (Exception)
        {
            // Deliberately dropped, cancellation included; see Announce.
        }
    }

    private static UpdateResult Untouched(
        UpdatePlan plan, UpdateAction action, string message, List<UpdateStep> applied) =>
        new(ExitCode.LocalAccessFailure, applied, Step(plan, action), message, null, null);

    /// Fetching and verifying happen here; the rest is the swap's.
    private static bool OnDisk(UpdateStep step) =>
        step.Action is UpdateAction.DiscardPrevious
            or UpdateAction.SetAside
            or UpdateAction.Install;

    private static UpdateStep Step(UpdatePlan plan, UpdateAction action) =>
        plan.Steps.First(step => step.Action == action);

    private static string Directory(string binaryPath) =>
        Path.GetDirectoryName(binaryPath) is { Length: > 0 } folder ? folder : ".";
}
