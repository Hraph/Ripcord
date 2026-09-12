using Ripcord.Domain.Inventory;

namespace Ripcord.Tests.Inventory;

/// The facts `ripcord check` reasons over, and the small amount of arithmetic that turns raw
/// readings into them. Every derivation lives here rather than in the adapter, because the
/// adapter cannot be run off Windows.
public class InventoryFactsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    /// A VHDX attached to the VM but absent from the relationship is a disk that will not be
    /// on the target. Path comparison is case-insensitive: Windows paths are.
    [Fact]
    public void A_vhdx_outside_the_relationship_is_reported()
    {
        VmFacts facts = Facts(
            disks: [Vhdx(@"D:\VMs\os.vhdx"), Vhdx(@"D:\VMs\data.vhdx")],
            replicated: [@"d:\vms\OS.VHDX"]);

        Assert.Equal([@"D:\VMs\data.vhdx"], facts.DisksOutsideReplication.Select(disk => disk.Path));
    }

    /// A pass-through disk is never in the relationship — Hyper-V Replica cannot carry one.
    /// Listing it as "missing from the relationship" would bury its own, louder rule.
    [Fact]
    public void A_passthrough_disk_is_not_reported_as_missing_from_the_relationship()
    {
        VmFacts facts = Facts(
            disks: [Vhdx(@"D:\VMs\os.vhdx"), new VmDisk(@"\\.\PHYSICALDRIVE2", true)],
            replicated: [@"D:\VMs\os.vhdx"]);

        Assert.Empty(facts.DisksOutsideReplication);
        Assert.Equal([@"\\.\PHYSICALDRIVE2"], facts.PassthroughDisks.Select(disk => disk.Path));
    }

    /// An unknown relationship disk set is not an empty one: every VHDX would read as missing.
    [Fact]
    public void An_unknown_relationship_disk_set_reports_nothing_rather_than_everything()
    {
        VmFacts facts = new(
            2048, 4096, 1024, [], [Vhdx(@"D:\VMs\os.vhdx")], null);

        Assert.Empty(facts.DisksOutsideReplication);
        Assert.False(facts.KnowsWhichDisksReplicate);
    }

    [Theory]
    [InlineData("00-15-5D-01-02-03", "00155d010203", true)]
    [InlineData("00:15:5D:01:02:03", "00-15-5D-01-02-03", true)]
    [InlineData("00-15-5D-01-02-03", "00-15-5D-01-02-04", false)]
    public void Macs_compare_by_value_whatever_the_separators(string left, string right, bool same)
    {
        Assert.Equal(same, MacAddress.Same(left, right));
    }

    /// Unknown on either side is not a match and not a mismatch: it is unanswerable, and the
    /// rule that asks has to say so rather than conclude either way.
    [Theory]
    [InlineData(null, "00-15-5D-01-02-03")]
    [InlineData("00-15-5D-01-02-03", "")]
    [InlineData(null, null)]
    public void An_unknown_mac_never_compares_equal(string? left, string? right)
    {
        Assert.False(MacAddress.Same(left, right));
        Assert.False(MacAddress.Known(left) && MacAddress.Known(right));
    }

    /// Usable RAM is what the host has minus what the management OS must keep. The reserve is
    /// the only part of the feasibility calculation that comes from configuration (D5).
    [Fact]
    public void Usable_ram_is_the_physical_ram_less_the_host_reserve()
    {
        Assert.Equal(8192, Host(physicalRamMb: 12288).UsableRamMb(4));
    }

    /// A reserve larger than the machine is a misconfiguration, not a negative capacity.
    [Fact]
    public void A_reserve_larger_than_the_machine_leaves_no_usable_ram()
    {
        Assert.Equal(0, Host(physicalRamMb: 2048).UsableRamMb(4));
    }

    [Fact]
    public void Unknown_physical_ram_yields_no_usable_ram_figure()
    {
        Assert.Null(Host(physicalRamMb: null).UsableRamMb(4));
    }

    /// `storage.data_volume` is written "D:" by hand and reported "D:" or "D:\" by Windows
    /// depending on the class; the operator should not have to know which.
    [Theory]
    [InlineData("D:")]
    [InlineData("d:")]
    [InlineData(@"D:\")]
    public void A_volume_is_found_whatever_the_spelling_of_its_drive(string asked)
    {
        HostFacts host = Host(volumes: [new HostVolume("D:", 500_000_000_000, null, null, null)]);

        Assert.NotNull(host.Volume(asked));
    }

    [Fact]
    public void An_absent_volume_is_null_rather_than_an_empty_one()
    {
        Assert.Null(Host(volumes: []).Volume("D:"));
    }

    [Fact]
    public void A_certificate_expiring_inside_the_window_is_flagged_and_one_outside_is_not()
    {
        CertificateFact certificate = new(
            "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555",
            "CN=HV-REPLICA-01",
            Now.AddDays(45));

        Assert.True(certificate.ExpiresWithin(TimeSpan.FromDays(60), Now));
        Assert.False(certificate.ExpiresWithin(TimeSpan.FromDays(30), Now));
    }

    /// An already expired certificate is inside every window, not outside them.
    [Fact]
    public void An_expired_certificate_is_inside_the_window()
    {
        CertificateFact certificate = new(
            "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555",
            "CN=HV-REPLICA-01",
            Now.AddDays(-1));

        Assert.True(certificate.ExpiresWithin(TimeSpan.FromDays(60), Now));
        Assert.True(certificate.HasExpiredAt(Now));
    }

    /// These records hold lists, and a record compares a list by reference. Two identical
    /// facts have to be equal — the snapshot round-trip is the first thing that trips on it.
    [Fact]
    public void Identical_facts_compare_equal_despite_holding_lists()
    {
        Assert.Equal(Facts(), Facts());
        Assert.Equal(Facts().GetHashCode(), Facts().GetHashCode());
        Assert.Equal(Host(), Host());
        Assert.Equal(Host().GetHashCode(), Host().GetHashCode());
    }

    [Fact]
    public void Facts_differing_only_inside_a_list_are_not_equal()
    {
        Assert.NotEqual(Facts(disks: [Vhdx(@"D:\a.vhdx")]), Facts(disks: [Vhdx(@"D:\b.vhdx")]));
        Assert.NotEqual(
            Host(volumes: [new HostVolume("C:", 1, null, null, null)]),
            Host(volumes: [new HostVolume("D:", 1, null, null, null)]));
    }

    private static VmDisk Vhdx(string path) => new(path, false);

    private static VmFacts Facts(
        IReadOnlyList<VmDisk>? disks = null, IReadOnlyList<string>? replicated = null) =>
        new(
            2048,
            4096,
            1024,
            [new VirtualAdapter("Network Adapter", "vSwitch-PROD", true, "00-15-5D-01-02-03", false, 10)],
            disks ?? [Vhdx(@"D:\VMs\os.vhdx")],
            replicated ?? [@"D:\VMs\os.vhdx"]);

    private static HostFacts Host(
        int? physicalRamMb = 12288, IReadOnlyList<HostVolume>? volumes = null) =>
        new(
            physicalRamMb,
            volumes ?? [new HostVolume("D:", 500_000_000_000, 2_000_000_000_000, true, true)],
            null);
}
