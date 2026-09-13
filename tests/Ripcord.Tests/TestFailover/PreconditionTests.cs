using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;
using Ripcord.Domain.TestFailover;
using Ripcord.Tests.Checks;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.TestFailover;

/// Step 1 of the sequence: `ripcord check`, as a gate. What makes this subtle is that
/// `CheckReport.HasCriticalViolation` is deliberately false for a rule that could not be
/// evaluated — code 1 means "a critical rule is violated", and overloading it with "could not
/// be checked" would make `check` unusable as the gate everything downstream needs.
///
/// So the gate is the union of two things: violated criticals, and the named set of rules
/// whose *unevaluability* bears on whether a test failover is safe. That set is a written-out
/// list rather than a predicate, because a list can be disagreed with by someone who was not
/// in the room and a predicate can only be reverse-engineered.
public class PreconditionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_clean_report_does_not_refuse()
    {
        Assert.False(Evaluate(Report()).Refuses);
    }

    [Fact]
    public void A_violated_critical_refuses_and_names_the_rule()
    {
        PreconditionRefusal refusal = Evaluate(Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { SwitchName = "vSwitch-OLD" })));

        Assert.True(refusal.Refuses);
        Assert.Contains(
            refusal.Criticals,
            finding => finding.Rule.Id == CheckRules.ReplicaSwitchMismatch);
    }

    /// The permanent pass-through critical on VM-BACKUP-01 is why the acknowledgement
    /// mechanism exists. Without this, the monthly test failover could never run at all.
    [Fact]
    public void An_actively_acknowledged_critical_does_not_refuse()
    {
        Assert.False(Evaluate(Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { SwitchName = "vSwitch-OLD" }),
            Acknowledged(CheckRules.ReplicaSwitchMismatch, "VM-DC-01", Now.AddDays(30))))
            .Refuses);
    }

    /// An acknowledgement that has run out is not an acknowledgement. The operator has to
    /// re-state the reason, which is the whole point of the mandatory expiry.
    [Fact]
    public void A_lapsed_acknowledgement_refuses_again()
    {
        Assert.True(Evaluate(Report(
            Pairs.Healthy(Now).WithTargetAdapter(
                "VM-DC-01", adapter => adapter with { SwitchName = "vSwitch-OLD" }),
            Acknowledged(CheckRules.ReplicaSwitchMismatch, "VM-DC-01", Now.AddDays(-1))))
            .Refuses);
    }

    /// The rule this whole class exists for: memory that could not be read is not memory
    /// that fits. A test VM consumes real RAM on a target with about twelve gigabytes.
    [Fact]
    public void An_unevaluable_capacity_rule_refuses()
    {
        PreconditionRefusal refusal = Evaluate(Report(
            Pairs.Healthy(Now).WithTarget(
                "VM-DC-01", facts => facts with { StartupRamMb = null })));

        Assert.True(refusal.Refuses);
        Assert.NotEmpty(refusal.Unevaluated);
    }

    /// And the counterweight: refusing on *every* unevaluable finding would make the tool
    /// unusable. An unreadable certificate expiry has nothing to do with whether an isolated
    /// copy boots.
    [Fact]
    public void An_unevaluable_rule_outside_the_list_does_not_refuse()
    {
        Assert.DoesNotContain(
            CheckRules.CertificateNearExpiry,
            TestFailoverPrecondition.BlockingWhenUnevaluated);
    }

    /// Acknowledgements apply to violations only, by design. An unevaluable finding carries
    /// no suppression, so one cannot be used to wave through a rule nobody could check —
    /// which would be an operator silencing an admission of ignorance.
    [Fact]
    public void An_acknowledgement_cannot_excuse_an_unevaluable_rule()
    {
        Assert.True(Evaluate(Report(
            Pairs.Healthy(Now).WithTarget(
                "VM-DC-01", facts => facts with { StartupRamMb = null }),
            Acknowledged(CheckRules.StartupRamExceedsTarget, "VM-DC-01", Now.AddDays(30))))
            .Refuses);
    }

    /// Unattended, the gate tightens rather than loosens. Nobody is watching, so a finding
    /// nobody could evaluate is not something an operator will glance at and judge — it stops
    /// the run. This is the counterweight to skipping the typed confirmation.
    [Fact]
    public void Unattended_any_unevaluable_finding_refuses()
    {
        CheckReport report = Report(Pairs.Healthy(Now).WithTargetHost(
            facts => facts with { Certificate = null }));

        Assert.False(TestFailoverPrecondition.Evaluate(report).Refuses);
        Assert.True(TestFailoverPrecondition.Evaluate(report, unattended: true).Refuses);
    }

    /// And a clean report is still clean: the tightening must not refuse a healthy pair.
    [Fact]
    public void Unattended_a_clean_report_still_does_not_refuse()
    {
        Assert.False(TestFailoverPrecondition.Evaluate(Report(), unattended: true).Refuses);
    }

    /// The list is the judgement, and it is meant to be read. Every entry names a rule that
    /// exists, so a rename cannot silently empty the gate.
    [Fact]
    public void Every_blocking_rule_is_a_rule_that_exists()
    {
        Assert.All(
            TestFailoverPrecondition.BlockingWhenUnevaluated,
            id => Assert.NotNull(CheckRules.ById(id)));
    }

    private static PreconditionRefusal Evaluate(CheckReport report) =>
        TestFailoverPrecondition.Evaluate(report);

    private static Acknowledgement Acknowledged(
        string ruleId, string vmName, DateTimeOffset expires) =>
        new(ruleId, vmName, "accepted for the test", expires);

    private static CheckReport Report(
        PairView? view = null, params Acknowledgement[] acknowledgements)
    {
        RipcordConfiguration configuration = Configurations.Create() with
        {
            Acknowledgements = acknowledgements,
        };

        return CheckEngine.Evaluate(new CheckRequest(
            view ?? Pairs.Healthy(Now), configuration, Now, []));
    }
}
