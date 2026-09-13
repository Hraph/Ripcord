using Ripcord.Domain.Checks;
using Ripcord.Domain.Failover;
using Ripcord.Tests.Checks;

namespace Ripcord.Tests.Failover;

/// Four operations, four different answers to "does this unknown make the outcome worse than
/// not acting at all". Whether a fact is peripheral is a property of the *pair* of fact and
/// operation, never of the fact alone: an unreadable free-space figure is peripheral to
/// whether an unplanned failover should proceed, and not remotely peripheral to whether the
/// VMs stay running once it has.
///
/// The lists are deliberately not factored together. The duplication between them is the
/// judgement, and collapsing it would put a rule in a class it does not belong to.
public class FailoverPreconditionTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    /// A planned failover is elective and postponing it is free. If Ripcord cannot tell
    /// whether the VM would come up with a network, it says so and stops.
    [Fact]
    public void A_planned_failover_refuses_when_the_network_could_not_be_read()
    {
        CheckReport report = Unevaluable(CheckRules.ReplicaSwitchMismatch);

        FailoverRefusal refusal =
            FailoverPrecondition.Evaluate(report, FailoverOperation.PlannedFailover);

        Assert.True(refusal.Refuses);
    }

    /// The same finding, the other operation. The primary is dead — that is what makes it
    /// unplanned — and refusing to fail over because a switch name could not be read would
    /// fail at the one thing the tool exists for. It proceeds and says what it could not see.
    [Fact]
    public void An_unplanned_failover_proceeds_on_the_very_same_finding()
    {
        CheckReport report = Unevaluable(CheckRules.ReplicaSwitchMismatch);

        FailoverRefusal refusal =
            FailoverPrecondition.Evaluate(report, FailoverOperation.UnplannedFailover);

        Assert.False(refusal.Refuses);
    }

    /// The D20 trap, in a mutating command rather than a reporting one. "Replication direction
    /// inverted" is true by definition while running on DR, so a failback that gated on it
    /// would be blocked by the very state it exists to repair.
    [Fact]
    public void A_failback_is_not_blocked_by_the_inverted_direction_it_exists_to_clear()
    {
        CheckReport report = Violated(CheckRules.ReplicationDirectionInverted);

        FailoverRefusal refusal =
            FailoverPrecondition.Evaluate(report, FailoverOperation.Failback);

        Assert.False(refusal.Refuses);
    }

    /// Reprotect restores replication after an unplanned failover, so it runs in the same
    /// inverted state and needs the same exemption.
    [Fact]
    public void A_reprotect_is_not_blocked_by_the_inverted_direction_either()
    {
        CheckReport report = Violated(CheckRules.ReplicationDirectionInverted);

        FailoverRefusal refusal =
            FailoverPrecondition.Evaluate(report, FailoverOperation.Reprotect);

        Assert.False(refusal.Refuses);
    }

    /// The exemption is scoped to the operations that need it. A planned failover running on a
    /// pair whose direction is already inverted is a genuine misconfiguration.
    [Fact]
    public void A_planned_failover_is_still_blocked_by_an_inverted_direction()
    {
        CheckReport report = Violated(CheckRules.ReplicationDirectionInverted);

        FailoverRefusal refusal =
            FailoverPrecondition.Evaluate(report, FailoverOperation.PlannedFailover);

        Assert.True(refusal.Refuses);
    }

    /// The exemption covers one rule, not the whole severity. A failback must still stop when
    /// something unrelated to the failed-over state is critically wrong.
    [Fact]
    public void A_failback_still_refuses_on_a_critical_it_does_not_exist_to_clear()
    {
        CheckReport report = Violated(CheckRules.ReplicaAdapterDisconnected);

        FailoverRefusal refusal =
            FailoverPrecondition.Evaluate(report, FailoverOperation.Failback);

        Assert.True(refusal.Refuses);
    }

    /// Refusing on every unevaluable finding is the other failure mode. An unreadable
    /// certificate expiry has no bearing on whether a VM boots on the target.
    [Fact]
    public void An_unreadable_certificate_expiry_blocks_nothing()
    {
        CheckReport report = Unevaluable(CheckRules.CertificateNearExpiry);

        Assert.All(
            Enum.GetValues<FailoverOperation>(),
            operation => Assert.False(
                FailoverPrecondition.Evaluate(report, operation).Refuses));
    }

    /// Reprotect writes a seeded initial replication onto the host that is now the replica. Not
    /// reprotecting leaves the pair unprotected, which is bad and stable; reprotecting into a
    /// volume that turns out to be full pauses the VMs currently serving production, which is
    /// worse. So this one unknown does stop it.
    [Fact]
    public void A_reprotect_refuses_when_free_space_could_not_be_read()
    {
        CheckReport report = Unevaluable(CheckRules.FreeSpaceBelowThreshold);

        FailoverRefusal refusal =
            FailoverPrecondition.Evaluate(report, FailoverOperation.Reprotect);

        Assert.True(refusal.Refuses);
    }

    /// The same unknown, during a disaster, does not. This pair of tests is the whole
    /// principle in two cases.
    [Fact]
    public void An_unplanned_failover_proceeds_when_free_space_could_not_be_read()
    {
        CheckReport report = Unevaluable(CheckRules.FreeSpaceBelowThreshold);

        FailoverRefusal refusal =
            FailoverPrecondition.Evaluate(report, FailoverOperation.UnplannedFailover);

        Assert.False(refusal.Refuses);
    }

    /// A refusal that does not say what to fix is a refusal nobody can act on, and this is the
    /// text the operator reads before deciding whether to override by hand.
    [Fact]
    public void A_refusal_carries_the_findings_that_caused_it()
    {
        CheckReport report = Unevaluable(CheckRules.ReplicaSwitchMismatch);

        FailoverRefusal refusal =
            FailoverPrecondition.Evaluate(report, FailoverOperation.PlannedFailover);

        Assert.NotEmpty(refusal.Unevaluated);
        Assert.Equal(
            CheckRules.ReplicaSwitchMismatch, refusal.Unevaluated[0].Rule.Id);
    }

    /// A healthy pair refuses nothing, for any operation. Without this the suite could pass
    /// with a precondition that always refuses.
    [Fact]
    public void A_healthy_pair_refuses_nothing_for_any_operation()
    {
        CheckReport report = Pairs.Evaluate(Pairs.Healthy(Now), Now);

        Assert.All(
            Enum.GetValues<FailoverOperation>(),
            operation => Assert.False(
                FailoverPrecondition.Evaluate(report, operation).Refuses,
                $"{operation} refused a healthy pair"));
    }

    /// An unplanned failover gates on nothing the report says, by design — but it is still
    /// reported, so the impact summary and the typed confirmation carry the whole story.
    /// Silence here would be the tool deciding on the operator's behalf.
    [Fact]
    public void An_unplanned_failover_still_reports_what_it_proceeded_past()
    {
        CheckReport report = Violated(CheckRules.ReplicaAdapterDisconnected);

        FailoverRefusal refusal =
            FailoverPrecondition.Evaluate(report, FailoverOperation.UnplannedFailover);

        Assert.False(refusal.Refuses);
        Assert.NotEmpty(refusal.Proceeded);
    }

    /// A fact nobody can read about a *different* VM does not stop this one moving. Without the
    /// scope the gate is unusable rather than merely strict: `VM-BACKUP-01` carries a
    /// pass-through disk Hyper-V Replica cannot replicate, so some of its facts are permanently
    /// unreadable — and an unscoped gate would block every planned failover of the domain
    /// controller for ever, for a reason that has nothing to do with the domain controller.
    [Fact]
    public void Another_vms_unreadable_fact_does_not_block_this_one()
    {
        CheckReport report = Unevaluable(CheckRules.ReplicaSwitchMismatch);

        FailoverRefusal refusal = FailoverPrecondition.Evaluate(
            report, FailoverOperation.PlannedFailover, "VM-LEGACY-01");

        Assert.False(refusal.Refuses);
    }

    /// And the same finding still blocks the VM it is actually about.
    [Fact]
    public void The_subjects_own_unreadable_fact_still_blocks_it()
    {
        CheckReport report = Unevaluable(CheckRules.ReplicaSwitchMismatch);

        FailoverRefusal refusal = FailoverPrecondition.Evaluate(
            report, FailoverOperation.PlannedFailover, "VM-DC-01");

        Assert.True(refusal.Refuses);
    }

    /// Host-level findings carry no subject and apply to every VM: free space on the target is
    /// not about one machine, and whichever VM moves lands on the same volume.
    [Fact]
    public void A_host_level_finding_applies_whichever_vm_is_moving()
    {
        CheckReport report = Unevaluable(CheckRules.FreeSpaceBelowThreshold, subject: null);

        FailoverRefusal refusal = FailoverPrecondition.Evaluate(
            report, FailoverOperation.PlannedFailover, "VM-LEGACY-01");

        Assert.True(refusal.Refuses);
    }

    /// The pair used here is fully readable, so a finding under test is the only one present
    /// and a test says exactly what it is about.
    private static CheckReport Unevaluable(string ruleId, string? subject = "VM-DC-01") =>
        Report(ruleId, FindingVerdict.Unevaluable, subject);

    private static CheckReport Violated(string ruleId) =>
        Report(ruleId, FindingVerdict.Violated);

    private static CheckReport Report(
        string ruleId, FindingVerdict verdict, string? subject = "VM-DC-01")
    {
        CheckReport healthy = Pairs.Evaluate(Pairs.Healthy(Now), Now);

        return healthy with
        {
            Findings =
            [
                new Finding(
                    CheckRules.ById(ruleId)!,
                    subject,
                    "under test",
                    "under test",
                    null,
                    verdict),
            ],
        };
    }
}
