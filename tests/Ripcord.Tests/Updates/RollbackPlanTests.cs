using Ripcord.Domain;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Failover;
using Ripcord.Domain.Updates;

namespace Ripcord.Tests.Updates;

/// Going back to the binary the last update set aside.
///
/// The decision is small on purpose: there is nothing to fetch, nothing to verify, and exactly
/// one binary to go back to. What is worth asserting is that it refuses when there is nothing
/// there, that it says what the operator is about to be left running, and that it carries the
/// same consequences an update does — because it is the same fact about the pair.
public class RollbackPlanTests
{
    private static readonly DateTimeOffset SetAside =
        new(2026, 9, 14, 8, 0, 0, TimeSpan.Zero);

    /// A host that has never updated has nothing set aside. Refused by name rather than
    /// reported as a plan with no steps: "there is nothing to go back to" and "going back
    /// changes nothing" are different answers.
    [Fact]
    public void With_no_binary_set_aside_there_is_nothing_to_go_back_to()
    {
        RollbackPlan plan = RollbackPlan.For(Subject(hasPrevious: false));

        Assert.False(plan.ChangesAnything);
        Assert.Contains("nothing to go back to", plan.Halt, StringComparison.Ordinal);
        Assert.Contains("`ripcord update`", plan.Halt!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_version_being_returned_to_is_named()
    {
        RollbackPlan plan = RollbackPlan.For(Subject());

        Assert.Equal("0.2.1", plan.PreviousVersion);
        Assert.Null(plan.Halt);
        Assert.Equal(3, plan.Steps.Count);
    }

    /// A file whose version cannot be read is still the file that was running here. Said
    /// rather than refused: refusing would strand a host on a release it is retreating from.
    [Fact]
    public void A_version_that_cannot_be_read_is_said_and_not_refused()
    {
        RollbackPlan plan = RollbackPlan.For(Subject(previousVersion: null));

        Assert.Null(plan.Halt);
        Assert.True(plan.ChangesAnything);
        Assert.Contains(
            plan.Warnings, warning => warning.Contains("could not be read", StringComparison.Ordinal));
    }

    /// Nothing in the sequence deletes. A running binary can be renamed and not removed, so a
    /// step that discarded one would be a step that fails on Windows and nowhere else.
    [Fact]
    public void Nothing_in_the_sequence_removes_a_file() =>
        Assert.DoesNotContain(
            RollbackPlan.For(Subject()).Steps,
            step => step.Description.Contains("delete", StringComparison.OrdinalIgnoreCase)
                || step.Description.Contains("discard", StringComparison.OrdinalIgnoreCase));

    /// The same consequence an update carries, because it is the same fact: the two hosts no
    /// longer agree, and a failover spanning both is refused while they do not.
    [Fact]
    public void Going_back_says_what_it_does_to_the_pair() =>
        Assert.Contains(
            RollbackPlan.For(Subject()).Warnings,
            warning => warning.Contains(
                "a failover spanning both is refused until HV-PRIMARY-01 matches",
                StringComparison.Ordinal)
                || warning.Contains(
                    "refused until HV-PRIMARY-01 does too", StringComparison.Ordinal));

    [Fact]
    public void A_pair_already_failed_over_is_told_what_this_delays() =>
        Assert.Contains(
            RollbackPlan.For(Subject(mode: OperatingMode.FailedOver)).Warnings,
            warning => warning.Contains("production is running here", StringComparison.Ordinal));

    /// The decision not to remember. `ripcord update` offers the newer release again, and an
    /// operator retreating from a bad one has to hear that from the tool rather than discover
    /// it the following week.
    [Fact]
    public void It_says_that_nothing_records_why() =>
        Assert.Contains(
            RollbackPlan.For(Subject()).Warnings,
            warning => warning.Contains(
                "will offer the newer release again", StringComparison.Ordinal));

    /// An earlier exchange that did not finish. Its file holds the only copy of a binary this
    /// host was running, so a second exchange would write over it — refused before anything
    /// else is weighed.
    [Fact]
    public void An_exchange_that_did_not_finish_stops_the_next_one()
    {
        RollbackPlan plan = RollbackPlan.For(Subject(interrupted: true));

        Assert.False(plan.ChangesAnything);
        Assert.Contains("did not finish", plan.Halt, StringComparison.Ordinal);
        Assert.Contains("only copy", plan.Halt!, StringComparison.Ordinal);
    }

    /// Refused even when there is a perfectly good binary to go back to: the file left behind
    /// is the thing at risk, not the one being returned to.
    [Fact]
    public void The_refusal_comes_before_anything_else_is_weighed() =>
        Assert.Contains(
            "did not finish",
            RollbackPlan.For(Subject(interrupted: true, hasPrevious: false)).Halt,
            StringComparison.Ordinal);

    [Fact]
    public void A_set_aside_binary_still_running_refuses_the_exchange_that_writes_over_it()
    {
        RollbackPlan plan = RollbackPlan.For(Subject() with { PreviousInUse = true });

        Assert.Empty(plan.Steps);
        Assert.Equal(UpdatePlan.PreviousStillRunning, plan.Halt);
    }

    private static RollbackSubject Subject(
        bool hasPrevious = true,
        string? previousVersion = "0.2.1",
        OperatingMode mode = OperatingMode.Normal,
        bool interrupted = false) =>
        new(
            interrupted,
            hasPrevious,
            previousVersion,
            hasPrevious ? SetAside : null,
            "0.3.0",
            VersionSkew.Between(new BuildIdentity("0.3.0", "abc123"), null),
            mode,
            "HV-PRIMARY-01");
}
