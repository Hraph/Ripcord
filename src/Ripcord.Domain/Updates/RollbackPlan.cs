using Ripcord.Domain.Checks;
using Ripcord.Domain.Failover;

namespace Ripcord.Domain.Updates;

/// What `ripcord rollback` is decided from. A value, like every other plan here, so the
/// decision is exercisable without a filesystem or a pair.
public sealed record RollbackSubject(
    /// A file left by an exchange that did not finish, holding a binary this host was
    /// running. Nothing else on the host knows what it is, so nothing else may write over it.
    bool HasInterruptedSwap,
    bool HasPreviousBinary,
    string? PreviousVersion,
    DateTimeOffset? SetAsideAt,
    string RunningVersion,
    VersionSkew Skew,
    OperatingMode Mode,
    string PeerHostName);

public sealed record RollbackStep(int Number, string Description, string Reason);

/// Putting back the binary the last update set aside.
///
/// It downloads nothing and verifies nothing, and both are the point: these two hosts are
/// meant to have no outbound access, so a retreat that needs the network is a retreat that
/// fails on the day it is needed.
///
/// What it puts back is the file `ripcord update` set aside, which is the binary this host was
/// last running. When that update fetched it, its signature was checked before it was
/// installed — but nothing here re-establishes that, and nothing here can: `.old` is a
/// filesystem convention, and a file somebody copied there by hand would be treated the same.
/// The claim this makes is the narrow one — these are the bytes that were in place before —
/// and not the broad one.
///
/// One generation, and no further. The update keeps exactly one binary aside, so this goes
/// back one version and says so rather than implying a history it does not have.
public sealed record RollbackPlan(
    IReadOnlyList<RollbackStep> Steps,
    string? PreviousVersion,
    DateTimeOffset? SetAsideAt,
    IReadOnlyList<string> Warnings,
    string? Halt)
{
    public bool ChangesAnything => this.Steps.Count > 0;

    public static RollbackPlan For(RollbackSubject subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        // Refused before anything else is weighed. The file holds the only copy of a binary
        // this host was running, and the exchange would write over it.
        if (subject.HasInterruptedSwap)
        {
            return Halted(
                "an earlier exchange did not finish and left a binary beside this one, under "
                + "'.swap'. It is the only copy of a version this host was running: move it "
                + "somewhere safe, and then run this again");
        }

        if (!subject.HasPreviousBinary)
        {
            return Halted(
                "there is no binary set aside on this host, so there is nothing to go back "
                + "to; one is kept by `ripcord update` and by nothing else");
        }

        // Not refused, said. The version is read off the file and a file that does not carry
        // one is a fact about the file rather than a reason to stop — the bytes are still the
        // ones that were running here.
        List<string> warnings = Consequences(subject);

        if (subject.PreviousVersion is null)
        {
            warnings.Insert(
                0,
                "the version of the binary set aside could not be read, so what this host "
                + "would be running afterwards is not established here");
        }

        return new RollbackPlan(
            Sequence(), subject.PreviousVersion, subject.SetAsideAt, warnings, null);
    }

    /// Three moves and no delete, and the reason is Windows: a running binary can be renamed
    /// but not removed, so the one being left behind cannot be discarded until something
    /// restarts. Swapping the two is what makes every step a rename — and it leaves the
    /// version being abandoned as the one kept aside, which is the honest place for it.
    private static IReadOnlyList<RollbackStep> Sequence() =>
    [
        new(1, "move the running binary out of the way",
            "a running binary can be renamed, never deleted, so nothing here removes one"),
        new(2, "put the binary set aside back where the running one was",
            "these are the bytes that were running on this host before the last update"),
        new(3, "keep the version being left behind as the one set aside",
            "running this again returns to it, so the retreat is not a one-way door"),
    ];

    /// The pair consequences are the update's, word for word, and the one thing this adds is
    /// the one thing the tool chose not to remember.
    private static List<string> Consequences(RollbackSubject subject)
    {
        List<string> warnings = PairConsequences.For(
            subject.Skew,
            subject.Mode,
            subject.PeerHostName,
            "once this host goes back",
            "does too",
            "going back now delays");

        // The release it is retreating from is still published, so the next `ripcord update`
        // offers it again. Said here because somebody going back needs to know the tool does
        // not remember why.
        warnings.Add(
            "nothing here records why: `ripcord update` will offer the newer release again");

        return warnings;
    }

    private static RollbackPlan Halted(string reason) => new([], null, null, [], reason);
}
