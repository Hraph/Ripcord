using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Checks;

/// Disks and volumes. The disk rules read the source's copy — that is where a disk gets
/// added without being included in the relationship. The volume rules read the target, which
/// is where the VMs would have to start.
public class StorageRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    private const long Gb = 1024L * 1024 * 1024;

    /// The permanent, accepted critical of this infrastructure (decision D19).
    [Fact]
    public void A_passthrough_disk_on_a_replicated_vm_is_critical()
    {
        Finding finding = Assert.Single(Report(WithPassthrough(Pairs.Healthy(Now)))
            .For(CheckRules.PassthroughDiskOnReplicatedVm));

        Assert.Equal(Severity.Critical, finding.Rule.Severity);
        Assert.Equal("VM-BACKUP-01", finding.Subject);
        Assert.Contains("PHYSICALDRIVE2", finding.Observed);
        Assert.Contains("starts without that disk", finding.Implication);
    }

    /// It can be acknowledged, unlike the memory criticals: the disk is a deliberate property
    /// of this infrastructure, and without an acknowledgement `check` would exit 1 forever.
    [Fact]
    public void The_passthrough_critical_is_acknowledgeable()
    {
        Assert.True(
            CheckRules.ById(CheckRules.PassthroughDiskOnReplicatedVm)!.Acknowledgeable);
    }

    /// A pass-through disk on a VM nobody replicates is not this rule's business:
    /// `vm-without-relationship` already says the louder thing about it.
    [Fact]
    public void A_passthrough_disk_on_an_unreplicated_vm_is_not_reported_here()
    {
        Assert.Empty(Report(
            WithPassthrough(Pairs.Healthy(Now)).WithSourceVm(
                "VM-BACKUP-01", vm => vm with { Role = ReplicationRole.None }))
            .For(CheckRules.PassthroughDiskOnReplicatedVm));
    }

    [Fact]
    public void A_vm_with_only_vhdx_disks_produces_no_finding()
    {
        Assert.Empty(Report(Pairs.Healthy(Now))
            .For(CheckRules.PassthroughDiskOnReplicatedVm));
    }

    [Fact]
    public void A_vhdx_absent_from_the_relationship_is_critical()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithSource("VM-DC-01", facts => facts with
            {
                Disks = [.. facts.Disks, new VmDisk(@"D:\VMs\VM-DC-01\data.vhdx", false)],
            }))
            .For(CheckRules.VhdxOutsideRelationship));

        Assert.Equal(Severity.Critical, finding.Rule.Severity);
        Assert.Contains("data.vhdx", finding.Observed);
        Assert.Contains("does not exist on the target", finding.Implication);
        Assert.False(finding.Rule.Acknowledgeable);
    }

    /// Windows paths are case-insensitive, and the two CIM classes do not agree on case.
    [Fact]
    public void A_disk_named_in_a_different_case_is_the_same_disk()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now).WithSource("VM-DC-01", facts => facts with
            {
                ReplicatedDiskPaths = [Pairs.Vhdx("VM-DC-01").ToUpperInvariant()],
            }))
            .For(CheckRules.VhdxOutsideRelationship));
    }

    /// A relationship that did not say which disks it carries is not a relationship that
    /// carries none — that reading would report every VHDX as missing.
    [Fact]
    public void An_unknown_relationship_disk_set_is_unevaluable_rather_than_empty()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithSource(
                "VM-DC-01", facts => facts with { ReplicatedDiskPaths = null }));

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.VhdxOutsideRelationship)).Verdict);
    }

    [Fact]
    public void An_unreadable_source_copy_leaves_both_disk_rules_unevaluable()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithSourceVm("VM-DC-01", vm => vm with { Facts = null }));

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.PassthroughDiskOnReplicatedVm)).Verdict);

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.VhdxOutsideRelationship)).Verdict);
    }

    [Fact]
    public void Free_space_below_the_threshold_is_a_warning()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithVolume(volume => volume with { FreeBytes = 100 * Gb }))
            .For(CheckRules.FreeSpaceBelowThreshold));

        Assert.Equal(Severity.Warning, finding.Rule.Severity);
        Assert.Contains("100 GB free", finding.Observed);
        Assert.Contains("200 GB threshold", finding.Observed);
        Assert.Contains("the VMs that started there pause", finding.Implication);
    }

    [Fact]
    public void Free_space_above_the_threshold_produces_no_finding()
    {
        Assert.Empty(Report(Pairs.Healthy(Now)).For(CheckRules.FreeSpaceBelowThreshold));
    }

    /// The volume is the target's, not this host's: `check` run on the primary judges the
    /// disaster recovery host's free space, because that is where the VMs would land.
    [Fact]
    public void The_volume_judged_is_the_targets()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithSourceHost(facts => facts with
            {
                Volumes = [new HostVolume("D:", 1 * Gb, 4000 * Gb, false, null)],
            }),
            document => document.Replication!.ExpectedRole = "primary");

        Assert.Single(report.For(CheckRules.FreeSpaceBelowThreshold));
    }

    [Fact]
    public void A_volume_that_could_not_be_read_is_unevaluable()
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithTargetHost(facts => facts with { Volumes = [] }));

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.FreeSpaceBelowThreshold)).Verdict);
    }

    /// The one rule with nothing to do with Hyper-V (COHERENCE B4): a locked volume means
    /// nothing starts until someone types a recovery key at the console, during the incident.
    [Fact]
    public void Bitlocker_without_auto_unlock_on_the_target_is_critical()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithVolume(
                volume => volume with { IsAutoUnlockEnabled = false }))
            .For(CheckRules.TargetBitlockerWithoutAutounlock));

        Assert.Equal(Severity.Critical, finding.Rule.Severity);
        Assert.Contains("recovery key", finding.Implication);
    }

    /// Carried from an administrator's earlier read by the publishing service: the finding
    /// says when it was true, not that it is true now.
    [Fact]
    public void A_carried_bitlocker_state_is_dated_in_the_finding()
    {
        Finding finding = Assert.Single(Report(
            Pairs.Healthy(Now).WithVolume(volume => volume with
            {
                IsAutoUnlockEnabled = false,
                BitLockerReadAt = new DateTimeOffset(2026, 9, 13, 9, 30, 0, TimeSpan.Zero),
            }))
            .For(CheckRules.TargetBitlockerWithoutAutounlock));

        Assert.Contains("as read 2026-09-13 09:30 UTC", finding.Observed, StringComparison.Ordinal);
    }

    [Fact]
    public void Bitlocker_with_auto_unlock_produces_no_finding()
    {
        Assert.Empty(Report(Pairs.Healthy(Now))
            .For(CheckRules.TargetBitlockerWithoutAutounlock));
    }

    [Fact]
    public void An_unencrypted_volume_produces_no_finding()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now).WithVolume(volume => volume with
            {
                IsBitLockerProtected = false,
                IsAutoUnlockEnabled = null,
            }))
            .For(CheckRules.TargetBitlockerWithoutAutounlock));
    }

    /// An unread encryption state is not an unencrypted volume (V12 — the auto-unlock answer
    /// is a method call, and a provider that does not expose it leaves this unknown).
    [Theory]
    [InlineData(null, null)]
    [InlineData(true, null)]
    public void An_unread_encryption_state_is_unevaluable(
        bool? isProtected, bool? autoUnlock)
    {
        CheckReport report = Report(
            Pairs.Healthy(Now).WithVolume(volume => volume with
            {
                IsBitLockerProtected = isProtected,
                IsAutoUnlockEnabled = autoUnlock,
            }));

        Assert.Equal(
            FindingVerdict.Unevaluable,
            Assert.Single(report.For(CheckRules.TargetBitlockerWithoutAutounlock)).Verdict);
    }

    /// Switched off in configuration, the rule says nothing at all — not even that it could
    /// not be evaluated. The operator declared it none of Ripcord's business.
    [Fact]
    public void With_the_bitlocker_check_switched_off_the_rule_is_silent()
    {
        Assert.Empty(Report(
            Pairs.Healthy(Now).WithVolume(volume => volume with { IsAutoUnlockEnabled = false }),
            document => document.Storage!.CheckBitlockerAutounlock = false)
            .For(CheckRules.TargetBitlockerWithoutAutounlock));
    }

    private static PairView WithPassthrough(PairView view) =>
        view.WithSource("VM-BACKUP-01", facts => facts with
        {
            Disks = [.. facts.Disks, new VmDisk(@"\\.\PHYSICALDRIVE2", true)],
        });

    private static CheckReport Report(
        PairView view, Action<ConfigurationDocument>? adjust = null) =>
        Pairs.Evaluate(view, Now, adjust);
}
