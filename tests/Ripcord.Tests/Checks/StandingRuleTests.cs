using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Checks;

/// The findings that are true whatever WMI says. Each one changes what the operator would do
/// on the day and none of them can be fixed by a command, which is why they carry no remedy
/// and are stated every run.
public class StandingRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    /// With no recovery history the replica is a thirty-second mirror, so the backup
    /// repository is the whole of the point-in-time protection — and it is the one disk
    /// Hyper-V Replica does not carry.
    [Fact]
    public void A_declared_passthrough_disk_is_reported_as_the_sole_point_in_time_copy()
    {
        Finding finding = Assert.Single(Report(Pairs.Healthy(Now))
            .For(CheckRules.PassthroughDiskIsSolePointInTimeCopy));

        Assert.Equal(Severity.Info, finding.Rule.Severity);
        Assert.Equal("VM-BACKUP-01", finding.Subject);
        Assert.Null(finding.Remedy);
        Assert.Contains("30-second mirror", finding.Implication);
    }

    /// Declared in configuration rather than observed: it is a statement about how this
    /// infrastructure is designed, not a reading that could come back different.
    [Fact]
    public void The_standing_finding_comes_from_the_configuration_not_from_wmi()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now),
            document => document.Vms![2].HasPassthroughDisk = false)
            .For(CheckRules.PassthroughDiskIsSolePointInTimeCopy));
    }

    [Fact]
    public void A_domain_controller_in_scope_is_reported_every_run()
    {
        Finding finding = Assert.Single(Report(Pairs.Healthy(Now))
            .For(CheckRules.DomainControllerPresent));

        Assert.Equal(Severity.Info, finding.Rule.Severity);
        Assert.Equal("VM-DC-01", finding.Subject);
        Assert.Contains("USN rollback", finding.Implication);
    }

    [Fact]
    public void With_no_domain_controller_declared_the_rule_is_silent()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now),
            document => document.Vms![0].IsDomainController = false)
            .For(CheckRules.DomainControllerPresent));
    }

    /// Nothing observable knows when Microsoft stops shipping patches, so the date is
    /// configuration — and the decision is due before it, not on it.
    [Fact]
    public void A_guest_whose_support_has_ended_is_reported()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now),
            document => document.Vms![1].GuestOsSupportEnds = new DateTime(2026, 1, 1))
            .For(CheckRules.GuestOsOutOfSupport));

        Assert.Equal(Severity.Info, finding.Rule.Severity);
        Assert.Equal("VM-LEGACY-01", finding.Subject);
        Assert.Contains("left support on 2026-01-01", finding.Observed);
    }

    [Fact]
    public void A_guest_approaching_the_end_of_support_is_reported_with_the_days_left()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now),
            document => document.Vms![1].GuestOsSupportEnds = new DateTime(2026, 10, 13))
            .For(CheckRules.GuestOsOutOfSupport));

        Assert.Contains("leaves support on 2026-10-13", finding.Observed);
        Assert.Contains("in 29 days", finding.Observed);
    }

    /// Far enough out that it is not yet a decision to take.
    [Fact]
    public void A_guest_supported_well_past_the_horizon_is_not_reported()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now),
            document => document.Vms![1].GuestOsSupportEnds = new DateTime(2030, 1, 1))
            .For(CheckRules.GuestOsOutOfSupport));
    }

    [Fact]
    public void With_no_date_configured_the_support_rule_is_silent()
    {
        Assert.Empty(Report(Pairs.Healthy(Now)).For(CheckRules.GuestOsOutOfSupport));
    }

    /// The disaster recovery window is when backups are most wanted, and it is exactly when
    /// they are not running.
    [Fact]
    public void While_failed_over_the_backup_gap_is_reported()
    {
        PairView view = Pairs.Healthy(Now);

        foreach (string name in Pairs.Names)
        {
            view = view
                .WithTargetVm(name, vm => vm with { Role = ReplicationRole.Primary })
                .WithSourceVm(name, vm => vm with { Role = ReplicationRole.Replica });
        }

        Finding finding = Assert.Single(Report(view)
            .For(CheckRules.NoBackupsWhileFailedOver));

        Assert.Equal(Severity.Info, finding.Rule.Severity);
        Assert.Contains(Pairs.Target, finding.Observed);
    }

    [Fact]
    public void In_normal_mode_the_backup_gap_is_not_reported()
    {
        Assert.Empty(Report(Pairs.Healthy(Now)).For(CheckRules.NoBackupsWhileFailedOver));
    }

    /// The exit criterion of the milestone: the three known anomalies of this infrastructure
    /// are each named by `check`.
    [Fact]
    public void The_three_known_anomalies_are_all_reported()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now),
            document => document.Vms![1].GuestOsSupportEnds = new DateTime(2026, 10, 13));

        Assert.Equal(
            ["VM-BACKUP-01", "VM-DC-01", "VM-LEGACY-01"],
            report.Of(Severity.Info)
                .Select(finding => finding.Subject)
                .OfType<string>()
                .Order());
    }

    private static CheckReport Report(
        PairView view, Action<ConfigurationDocument>? adjust = null) =>
        Pairs.Evaluate(view, Now, adjust);
}
