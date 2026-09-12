using Ripcord.Domain;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Checks;

/// The engine itself: which host is which, what mode the pair is in, how an acknowledgement
/// is applied, and what all that adds up to as an exit code.
public class CheckEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    /// The baseline every other test breaks one fact of. A correct pair produces the standing
    /// information and nothing else — no critical, no warning, and nothing unevaluated.
    [Fact]
    public void A_correct_pair_produces_only_the_standing_information()
    {
        CheckReport report = Pairs.Evaluate(Pairs.Healthy(Now), Now);

        Assert.Empty(report.Of(Severity.Critical));
        Assert.Empty(report.Of(Severity.Warning));
        Assert.Empty(report.Unevaluated);
        Assert.Equal(ExitCode.Success, report.Code);

        Assert.Equal(
            [CheckRules.PassthroughDiskIsSolePointInTimeCopy, CheckRules.DomainControllerPresent],
            report.Of(Severity.Info).Select(finding => finding.Rule.Id));
    }

    /// `expected_role: replica` means this host is where a failover would land. Getting that
    /// backwards would invert every cross-host rule at once.
    [Fact]
    public void The_target_is_the_host_that_normally_holds_the_replicas()
    {
        CheckReport report = Pairs.Evaluate(Pairs.Healthy(Now), Now);

        Assert.Equal(Pairs.Target, report.TargetHostName);
        Assert.Equal(Pairs.Source, report.SourceHostName);
        Assert.True(report.TargetIsLocal);
    }

    [Fact]
    public void Run_on_the_primary_the_target_is_the_peer()
    {
        CheckReport report = Pairs.Evaluate(
            Pairs.Healthy(Now),
            Now,
            document => document.Replication!.ExpectedRole = "primary");

        Assert.Equal(Pairs.Source, report.TargetHostName);
        Assert.False(report.TargetIsLocal);
    }

    [Fact]
    public void A_pair_running_as_designed_is_in_normal_mode()
    {
        Assert.Equal(OperatingMode.Normal, Pairs.Evaluate(Pairs.Healthy(Now), Now).Mode);
    }

    /// Derived from observed roles, never from configuration (decision D20): a P1 primary copy
    /// running on the host that normally holds the replicas means the pair is failed over.
    [Fact]
    public void A_p1_primary_on_the_target_puts_the_pair_in_failed_over_mode()
    {
        PairView view = Inverted(Pairs.Healthy(Now));

        Assert.Equal(OperatingMode.FailedOver, Pairs.Evaluate(view, Now).Mode);
    }

    /// The trap decision D20 exists to close: while running on the disaster recovery side the
    /// direction *is* inverted by definition, so a permanent critical there would make
    /// `check` red for the whole incident and block the failback meant to end it.
    [Fact]
    public void Failed_over_mode_suppresses_the_direction_rule_and_still_exits_zero()
    {
        CheckReport report = Pairs.Evaluate(Inverted(Pairs.Healthy(Now)), Now);

        Assert.Empty(report.For(CheckRules.ReplicationDirectionInverted));
        Assert.Equal(ExitCode.Success, report.Code);
    }

    /// A target that cannot be read says nothing either way about the mode: if this host is
    /// the primary and the disaster recovery host is offline, the pair is degraded, not
    /// failed over.
    [Fact]
    public void An_unreadable_target_is_not_read_as_failed_over()
    {
        CheckReport report = Pairs.Evaluate(
            Pairs.Healthy(Now).WithUnreachableSource(Now.AddMinutes(-5)),
            Now,
            document => document.Replication!.ExpectedRole = "primary");

        Assert.Equal(OperatingMode.Normal, report.Mode);
    }

    [Fact]
    public void A_violated_critical_rule_exits_one()
    {
        CheckReport report = Pairs.Evaluate(Disconnected(Pairs.Healthy(Now)), Now);

        Assert.Equal(ExitCode.CriticalFinding, report.Code);
    }

    /// Warnings never change the exit code: a scheduled task that failed on a warning gets
    /// switched off within a week, and then the criticals go unread too.
    [Fact]
    public void A_warning_does_not_change_the_exit_code()
    {
        CheckReport report = Pairs.Evaluate(
            Pairs.Healthy(Now).WithVolume(volume => volume with { FreeBytes = 1 }),
            Now);

        Assert.NotEmpty(report.Of(Severity.Warning));
        Assert.Equal(ExitCode.Success, report.Code);
    }

    /// A rule that could not be checked never sets code 1 either — that code means "a
    /// critical rule is violated" (decision D7), and overloading it would make `check`
    /// unusable as the gate the later milestones depend on.
    [Fact]
    public void An_unevaluable_critical_rule_does_not_exit_one()
    {
        CheckReport report = Pairs.Evaluate(
            Pairs.Healthy(Now).WithUnreachableSource(Now.AddMinutes(-5)),
            Now);

        Assert.NotEmpty(report.Unevaluated);
        Assert.Empty(report.Of(Severity.Critical));
        Assert.Equal(ExitCode.Success, report.Code);
    }

    /// The permanent, accepted, unfixable critical of this infrastructure. Without this the
    /// command exits 1 forever and milestones 3 and 5 never work.
    [Fact]
    public void An_active_acknowledgement_keeps_the_finding_and_clears_the_exit_code()
    {
        CheckReport report = Pairs.Evaluate(
            WithPassthrough(Pairs.Healthy(Now)),
            Now,
            Acknowledging(new DateTime(2027, 1, 1)));

        Finding finding = Assert.Single(
            report.For(CheckRules.PassthroughDiskOnReplicatedVm));

        Assert.Equal(FindingVerdict.Violated, finding.Verdict);
        Assert.True(finding.Suppression!.IsActive);
        Assert.Equal("accepted by design", finding.Suppression.Acknowledgement.Reason);
        Assert.Equal(ExitCode.Success, report.Code);
    }

    /// The expiry is what stops an acknowledgement from becoming a rule deleted by the back
    /// door. Once it lapses the finding counts again — and says why it came back.
    [Fact]
    public void A_lapsed_acknowledgement_is_shown_and_counts_again()
    {
        CheckReport report = Pairs.Evaluate(
            WithPassthrough(Pairs.Healthy(Now)),
            Now,
            Acknowledging(new DateTime(2026, 1, 1)));

        Finding finding = Assert.Single(
            report.For(CheckRules.PassthroughDiskOnReplicatedVm));

        Assert.False(finding.Suppression!.IsActive);
        Assert.Equal(ExitCode.CriticalFinding, report.Code);
    }

    /// Acknowledging one VM must not silence the other two, and a host-scoped entry must not
    /// silence a per-VM finding.
    [Fact]
    public void An_acknowledgement_covers_only_the_vm_it_names()
    {
        PairView view = WithPassthrough(Pairs.Healthy(Now), "VM-DC-01", "VM-BACKUP-01");

        CheckReport report = Pairs.Evaluate(view, Now, Acknowledging(new DateTime(2027, 1, 1)));

        Assert.Equal(
            [("VM-BACKUP-01", true), ("VM-DC-01", false)],
            report.For(CheckRules.PassthroughDiskOnReplicatedVm)
                .Select(finding => (finding.Subject, finding.Suppression is { IsActive: true })));

        Assert.Equal(ExitCode.CriticalFinding, report.Code);
    }

    /// Accepting a rule that could not be evaluated would suppress the admission that it was
    /// never checked — the one thing worse than the finding itself.
    [Fact]
    public void An_acknowledgement_never_suppresses_an_unevaluable_finding()
    {
        CheckReport report = Pairs.Evaluate(
            Pairs.Healthy(Now).WithSourceVm("VM-BACKUP-01", vm => vm with { Facts = null }),
            Now,
            Acknowledging(new DateTime(2027, 1, 1)));

        Finding finding = Assert.Single(
            report.For(CheckRules.PassthroughDiskOnReplicatedVm));

        Assert.Equal(FindingVerdict.Unevaluable, finding.Verdict);
        Assert.Null(finding.Suppression);
    }

    /// Read top to bottom under pressure, so criticals come before warnings before
    /// information, whatever order the rules happened to run in.
    [Fact]
    public void Findings_come_out_in_severity_order()
    {
        CheckReport report = Pairs.Evaluate(
            Disconnected(Pairs.Healthy(Now)).WithVolume(volume => volume with { FreeBytes = 1 }),
            Now);

        Assert.Equal(
            report.Violations.Select(finding => finding.Rule.Severity).Order(),
            report.Violations.Select(finding => finding.Rule.Severity));
    }

    /// Every finding has to name what it means on the day, not only what was read: "incorrect
    /// vSwitch" helps nobody, "this VM will boot with no network on the target" triggers
    /// action.
    [Fact]
    public void Every_finding_states_an_implication()
    {
        CheckReport report = Pairs.Evaluate(Disconnected(Pairs.Healthy(Now)), Now);

        Assert.All(report.Findings, finding =>
        {
            Assert.NotEmpty(finding.Observed);
            Assert.NotEmpty(finding.Implication);
        });
    }

    /// A degradation the Application noticed travels through to the report: the rules that
    /// needed the missing facts say they could not be checked, and this says why.
    [Fact]
    public void The_notes_it_was_given_come_out_with_the_report()
    {
        CheckReport report = Pairs.Evaluate(
            Pairs.Healthy(Now), Now, notes: ["the host system could not be read: cimv2 down"]);

        Assert.Contains("cimv2 down", Assert.Single(report.Notes));
    }

    private static Action<ConfigurationDocument> Acknowledging(DateTime expires) =>
        document => document.Checks = new ChecksDocument
        {
            Acknowledgements =
            [
                new AcknowledgementDocument
                {
                    Rule = CheckRules.PassthroughDiskOnReplicatedVm,
                    Vm = "VM-BACKUP-01",
                    Reason = "accepted by design",
                    Expires = expires,
                },
            ],
        };

    private static PairView WithPassthrough(PairView view, params string[] names)
    {
        foreach (string name in names.Length == 0 ? ["VM-BACKUP-01"] : names)
        {
            view = view.WithSource(name, facts => facts with
            {
                Disks = [.. facts.Disks, new VmDisk(@"\\.\PHYSICALDRIVE2", true)],
            });
        }

        return view;
    }

    private static PairView Disconnected(PairView view) =>
        view.WithTargetAdapter("VM-DC-01", adapter => adapter with { SwitchName = null });

    /// Every P1 copy primary on the disaster recovery host: what the pair looks like during
    /// an incident.
    private static PairView Inverted(PairView view)
    {
        foreach (string name in Pairs.Names)
        {
            view = view
                .WithTargetVm(name, vm => vm with { Role = ReplicationRole.Primary })
                .WithSourceVm(name, vm => vm with { Role = ReplicationRole.Replica });
        }

        return view;
    }
}
