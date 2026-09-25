using Ripcord.Domain.Checks;
using Ripcord.Domain.Failover;

namespace Ripcord.Domain.Updates;

/// One move the update makes. `DiscardPrevious` and `SetAside` look like housekeeping and are
/// not: `SetAside` is the first step that cannot be undone by deleting a file, and everything
/// before it leaves the host exactly as it was found.
public enum UpdateAction
{
    Download,
    Verify,
    DiscardPrevious,
    SetAside,
    Install,
}

public sealed record UpdateStep(int Number, UpdateAction Action, string Description, string Reason);

/// What the plan is decided from. A value, so the decision is exercisable without a network,
/// a filesystem or a pair.
public sealed record UpdateSubject(
    UpdateStatus Status,
    bool InstallAllowed,
    VersionSkew Skew,
    OperatingMode Mode,
    string PeerHostName,

    /// The binary an earlier update set aside is still running, so it cannot be discarded.
    bool PreviousInUse = false);

/// What `ripcord update` would do, decided before anything is fetched — the same separation
/// `ripcord service install` makes between `Plan` and `Apply`, for the same reason: the plan is what
/// `--dry-run` prints, and what the operator is agreeing to when they type the node name.
///
/// `Halt` is a refusal with its reason. `Warnings` are consequences the operator has to weigh
/// and is still allowed to accept — updating one host of a pair is legitimate and it does make
/// the two disagree, so the command says so rather than deciding on their behalf.
public sealed record UpdatePlan(
    IReadOnlyList<UpdateStep> Steps,
    UpdateStatus Status,
    IReadOnlyList<string> Warnings,
    string? Halt)
{
    public bool ChangesAnything => this.Steps.Count > 0;

    public static UpdatePlan For(UpdateSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        // Refused by name rather than quietly doing nothing: a host somebody believes is
        // updating and is not is worse than one that says it will not.
        if (!subject.InstallAllowed)
        {
            return Halted(
                subject.Status,
                "installing is switched off in the configuration (updates.install); "
                + "this host may look for a release but not replace itself");
        }

        // A version that cannot be read cannot be compared, and a binary that replaces itself
        // on a guess is the one outcome worse than never updating.
        if (subject.Status.Verdict == UpdateVerdict.NotComparable)
        {
            return Halted(subject.Status, subject.Status.Explanation);
        }

        if (subject.Status.Verdict != UpdateVerdict.UpdateAvailable)
        {
            return new UpdatePlan([], subject.Status, [], null);
        }

        // Refused before the download: the step that discards it would fail with a bare
        // access-denied after the new release was fetched and verified.
        if (subject.PreviousInUse)
        {
            return Halted(subject.Status, PreviousStillRunning);
        }

        return new UpdatePlan(Sequence(), subject.Status, Consequences(subject), null);
    }

    /// Shared with `RollbackPlan`, which writes over the same file.
    /// The command first, so no console width splits it.
    public const string PreviousStillRunning =
        "run 'ripcord service restart' first, then this again. The binary the last update "
        + "set aside (ripcord.exe.old) is still running - the listener service, not "
        + "restarted since that update - and Windows will not replace a running binary";

    private static IReadOnlyList<UpdateStep> Sequence() =>
    [
        new(1, UpdateAction.Download,
            "download the release and its signature",
            "both are fetched before anything on this host is touched"),
        new(2, UpdateAction.Verify,
            "check the signature against the key compiled into this binary",
            "a release that cannot be shown to be genuine is not installed"),
        new(3, UpdateAction.DiscardPrevious,
            "discard the binary left by an earlier update",
            "the copy about to be set aside is the one worth keeping"),
        new(4, UpdateAction.SetAside,
            "move the running binary aside, keeping it",
            "it is what this host goes back to if the last step fails"),
        new(5, UpdateAction.Install,
            "put the new binary where the running one was",
            "the new version starts on the next service start, not now"),
    ];

    /// Consequences, not refusals. The operator is allowed to update a pair that is failed
    /// over — they may be updating precisely because of what went wrong — and what they are
    /// not allowed to do is find out afterwards.
    private static List<string> Consequences(UpdateSubject subject) =>
        PairConsequences.For(
            subject.Skew,
            subject.Mode,
            subject.PeerHostName,
            "once this host updates",
            "is updated too",
            "updating now delays");

    private static UpdatePlan Halted(UpdateStatus status, string reason) =>
        new([], status, [], reason);
}
