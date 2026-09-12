using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Checks;

/// Replication itself: running, keeping up, and the right way round. The per-VM figures come
/// from the local host, whose own relationship state is always readable — the peer's arrives
/// in a snapshot that may be stale or absent.
public class ReplicationRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    /// Health flickers to Warning for a single missed cycle, so the threshold is the
    /// configured one rather than zero: five minutes by default.
    [Fact]
    public void Degraded_health_for_longer_than_the_threshold_is_a_warning()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetVm("VM-DC-01", vm => vm with
            {
                Health = ReplicationHealth.Critical,
                LastReplicationTime = Now.AddMinutes(-30),
            }))
            .For(CheckRules.ReplicationHealthDegraded));

        Assert.Equal(Severity.Warning, finding.Rule.Severity);
        Assert.Contains("Critical", finding.Observed);
        Assert.Contains("has not replicated for", finding.Observed);
    }

    [Fact]
    public void Degraded_health_inside_the_threshold_is_treated_as_a_blip()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now).WithTargetVm(
                "VM-DC-01", vm => vm with { Health = ReplicationHealth.Warning }))
            .For(CheckRules.ReplicationHealthDegraded));
    }

    [Fact]
    public void Normal_health_produces_no_finding()
    {
        Assert.Empty(Report(Pairs.Healthy(Now)).For(CheckRules.ReplicationHealthDegraded));
    }

    /// A relationship that reports no health at all is not a healthy relationship.
    [Fact]
    public void A_relationship_reporting_no_health_is_unevaluable()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithTargetVm(
                "VM-DC-01", vm => vm with { Health = ReplicationHealth.Unknown }));

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.ReplicationHealthDegraded)).Verdict);
    }

    /// A VM with no relationship has no health to judge — the info rule covers it instead.
    [Fact]
    public void A_vm_with_no_relationship_is_not_judged_on_its_health()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now).WithTargetVm("VM-DC-01", vm => vm with
            {
                Role = ReplicationRole.None,
                Health = ReplicationHealth.Unknown,
            }))
            .For(CheckRules.ReplicationHealthDegraded));
    }

    /// Three times a thirty-second frequency is ninety seconds.
    [Fact]
    public void Lag_beyond_the_configured_multiple_of_the_frequency_is_a_warning()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetVm(
                "VM-DC-01", vm => vm with { LastReplicationTime = Now.AddMinutes(-10) }))
            .For(CheckRules.LagBeyondThreshold));

        Assert.Equal(Severity.Warning, finding.Rule.Severity);
        Assert.Contains("3 × 30s", finding.Observed);
        Assert.Contains("loses everything written since then", finding.Implication);
    }

    [Fact]
    public void Lag_inside_the_threshold_produces_no_finding()
    {
        Assert.Empty(Report(Pairs.Healthy(Now)).For(CheckRules.LagBeyondThreshold));
    }

    /// Never replicated is not a lag of zero: there is no copy on the other side at all.
    [Fact]
    public void A_relationship_that_has_never_replicated_is_reported_as_such()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetVm(
                "VM-DC-01", vm => vm with { LastReplicationTime = null }))
            .For(CheckRules.LagBeyondThreshold));

        Assert.Contains("never replicated", finding.Observed);
        Assert.Contains("Start-VMInitialReplication", finding.Remedy);
    }

    [Theory]
    [InlineData(ReplicationState.Resynchronizing)]
    [InlineData(ReplicationState.WaitingToStartResynchronization)]
    [InlineData(ReplicationState.ResynchronizationSuspended)]
    public void A_resynchronization_is_a_warning(ReplicationState state)
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetVm("VM-DC-01", vm => vm with { State = state }))
            .For(CheckRules.ResynchronizationInProgress));

        Assert.Equal(Severity.Warning, finding.Rule.Severity);
        Assert.Contains("weeks with nobody noticing", finding.Implication);
    }

    [Fact]
    public void A_replicating_vm_is_not_resynchronizing()
    {
        Assert.Empty(Report(Pairs.Healthy(Now))
            .For(CheckRules.ResynchronizationInProgress));
    }

    /// A partial inversion: one VM already primary on the disaster recovery host while the
    /// rest of the pair is not. That is maintenance nobody switched back.
    [Fact]
    public void One_vm_primary_on_the_target_while_the_others_are_not_is_critical()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetVm(
                "VM-BACKUP-01", vm => vm with { Role = ReplicationRole.Primary }))
            .For(CheckRules.ReplicationDirectionInverted));

        Assert.Equal(Severity.Critical, finding.Rule.Severity);
        Assert.Equal("VM-BACKUP-01", finding.Subject);
        Assert.Contains("split the estate", finding.Implication);
    }

    /// VM-BACKUP-01 is P2, so inverting it alone does not put the pair in failed-over mode —
    /// which is exactly why the partial inversion is still reported.
    [Fact]
    public void A_partial_inversion_does_not_put_the_pair_in_failed_over_mode()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithTargetVm(
                "VM-BACKUP-01", vm => vm with { Role = ReplicationRole.Primary }));

        Assert.Equal(OperatingMode.Normal, report.Mode);
    }

    [Fact]
    public void A_correct_direction_produces_no_finding()
    {
        Assert.Empty(Report(Pairs.Healthy(Now))
            .For(CheckRules.ReplicationDirectionInverted));
    }

    [Fact]
    public void A_target_that_could_not_be_read_leaves_the_direction_unevaluable()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithUnreachableSource(Now.AddMinutes(-5)),
            document => document.Replication!.ExpectedRole = "primary");

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.ReplicationDirectionInverted)).Verdict);
    }

    /// Observed on the source rather than filtered through the configured list: a VM nobody
    /// declared and nobody replicates is exactly what the operator needs to notice.
    [Fact]
    public void A_vm_on_the_source_with_no_relationship_is_information()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithSourceVm(
                "VM-LEGACY-01", vm => vm with { Role = ReplicationRole.None }))
            .For(CheckRules.VmWithoutRelationship));

        Assert.Equal(Severity.Info, finding.Rule.Severity);
        Assert.Contains("declared in ripcord.yaml", finding.Observed);
        Assert.Contains("leaves it behind", finding.Implication);
    }

    [Fact]
    public void An_undeclared_vm_with_no_relationship_is_reported_too()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now) with
            {
                Peer = Pairs.SourceHost(Now) with
                {
                    Vms =
                    [
                        .. Pairs.SourceHost(Now).Vms,
                        new VmReplicationState(
                            "VM-SCRATCH-01",
                            ReplicationRole.None,
                            ReplicationState.Disabled,
                            ReplicationHealth.Unknown,
                            null,
                            null),
                    ],
                },
            })
            .For(CheckRules.VmWithoutRelationship));

        Assert.Equal("VM-SCRATCH-01", finding.Subject);
        Assert.DoesNotContain("declared in ripcord.yaml", finding.Observed);
    }

    [Fact]
    public void A_source_that_could_not_be_read_leaves_the_inventory_unevaluable()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithUnreachableSource(Now.AddMinutes(-5)));

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.VmWithoutRelationship)).Verdict);
    }

    [Fact]
    public void A_certificate_inside_the_sixty_day_window_is_a_warning()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetHost(facts => facts with
            {
                Certificate = new CertificateFact(
                    "AAAA", $"CN={Pairs.Target}", Now.AddDays(45)),
            }))
            .For(CheckRules.CertificateNearExpiry));

        Assert.Equal(Severity.Warning, finding.Rule.Severity);
        Assert.Contains("expires on", finding.Observed);
        Assert.Contains("replication stops", finding.Implication);
    }

    [Fact]
    public void An_already_expired_certificate_says_so()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithTargetHost(facts => facts with
            {
                Certificate = new CertificateFact(
                    "AAAA", $"CN={Pairs.Target}", Now.AddDays(-1)),
            }))
            .For(CheckRules.CertificateNearExpiry));

        Assert.Contains("expired on", finding.Observed);
    }

    /// Both hosts' certificates are judged, each found in its own store and carried in its
    /// own snapshot: the pair dies when either one expires.
    [Fact]
    public void The_peers_certificate_is_judged_as_well_as_this_hosts()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithSourceHost(facts => facts with
            {
                Certificate = new CertificateFact(
                    "BBBB", $"CN={Pairs.Source}", Now.AddDays(10)),
            }))
            .For(CheckRules.CertificateNearExpiry));

        Assert.Contains(Pairs.Source, finding.Observed);
    }

    [Fact]
    public void A_certificate_well_beyond_the_window_produces_no_finding()
    {
        Assert.Empty(Report(Pairs.Healthy(Now)).For(CheckRules.CertificateNearExpiry));
    }

    /// A thumbprint naming nothing in the store is a finding, not a crash: the operator
    /// configured a certificate that is not there.
    [Fact]
    public void A_configured_certificate_that_is_absent_is_unevaluable()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithTargetHost(facts => facts with { Certificate = null }));

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.CertificateNearExpiry)).Verdict);
    }

    /// With the listener off there is no configured certificate, so the rule has nothing to
    /// judge and says nothing — not even that it could not be judged.
    [Fact]
    public void With_the_listener_disabled_the_certificate_rule_is_silent()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now).WithTargetHost(facts => facts with { Certificate = null }),
            document => document.Listener!.Enabled = false)
            .For(CheckRules.CertificateNearExpiry));
    }

    private static CheckReport Report(
        PairView view, Action<ConfigurationDocument>? adjust = null) =>
        Pairs.Evaluate(view, Now, adjust);
}
