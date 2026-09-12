using Ripcord.Domain.TestFailover;

namespace Ripcord.Tests.TestFailover;

/// A test VM left behind by an interrupted run holds disk on the target and quietly breaks
/// the next test. Hyper-V shows them — `Get-VM` and both consoles do — what it never does is
/// tell anyone. That is the gap this rule fills.
///
/// Test VMs are discriminated on `ReplicationMode = TestReplica (3)`, never on the `" - Test"`
/// name suffix: the suffix is documented inconsistently and its localisation on a non-English
/// host is unverifiable, the same locale trap the firewall rule names already carry.
public class OrphanTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan LingersAfter = TimeSpan.FromHours(4);

    [Fact]
    public void A_test_vm_younger_than_the_threshold_is_not_reported()
    {
        Assert.Empty(Detect(TestVm("VM-DC-01", Now.AddHours(-1))));
    }

    [Fact]
    public void A_test_vm_older_than_the_threshold_is_reported_with_its_age()
    {
        Orphan orphan = Assert.Single(Detect(TestVm("VM-DC-01", Now.AddHours(-9))));

        Assert.Equal("VM-DC-01", orphan.Name);
        Assert.Equal(TimeSpan.FromHours(9), orphan.Age);
        Assert.Equal(OrphanVerdict.Lingering, orphan.Verdict);
    }

    /// A run in progress is exactly the threshold's own duration old at the moment it is
    /// still legitimate. Reporting it would make the tool cry wolf against itself.
    [Fact]
    public void A_test_vm_exactly_at_the_threshold_is_not_yet_lingering()
    {
        Assert.Empty(Detect(TestVm("VM-DC-01", Now - LingersAfter)));
    }

    /// The creation time could not be read, so the age is unknown — which is not young. A
    /// test VM of unknown age is exactly the state an interrupted run leaves behind.
    [Fact]
    public void A_test_vm_of_unknown_age_is_reported_rather_than_assumed_recent()
    {
        Orphan orphan = Assert.Single(Detect(TestVm("VM-DC-01", createdAt: null)));

        Assert.Null(orphan.Age);
        Assert.Equal(OrphanVerdict.AgeUnknown, orphan.Verdict);
    }

    [Fact]
    public void A_host_with_no_test_vms_reports_nothing()
    {
        Assert.Empty(Detect());
    }

    /// Oldest first, so the report is stable from one run to the next and the worst offender
    /// is the first line read.
    [Fact]
    public void Orphans_are_reported_oldest_first()
    {
        IReadOnlyList<Orphan> orphans = Detect(
            TestVm("VM-LEGACY-01", Now.AddHours(-6)),
            TestVm("VM-DC-01", Now.AddHours(-30)));

        Assert.Equal(["VM-DC-01", "VM-LEGACY-01"], orphans.Select(orphan => orphan.Name));
    }

    /// An unknown age sorts ahead of every known one: it cannot be ranked against them, and
    /// burying it under a list of dated entries is how it goes unread.
    [Fact]
    public void An_unknown_age_is_reported_before_the_dated_ones()
    {
        IReadOnlyList<Orphan> orphans = Detect(
            TestVm("VM-LEGACY-01", Now.AddHours(-30)),
            TestVm("VM-DC-01", createdAt: null));

        Assert.Equal("VM-DC-01", orphans[0].Name);
    }

    /// A creation time in the future is a clock disagreement, not a young VM. Reading it as
    /// young would suppress the report on precisely the host whose clock cannot be trusted.
    [Fact]
    public void A_creation_time_in_the_future_is_reported_as_unknown()
    {
        Orphan orphan = Assert.Single(Detect(TestVm("VM-DC-01", Now.AddHours(2))));

        Assert.Equal(OrphanVerdict.AgeUnknown, orphan.Verdict);
    }

    private static IReadOnlyList<Orphan> Detect(params TestVm[] testVms) =>
        Orphans.Detect(testVms, LingersAfter, Now);

    private static TestVm TestVm(string name, DateTimeOffset? createdAt) =>
        new(name, createdAt, []);
}
