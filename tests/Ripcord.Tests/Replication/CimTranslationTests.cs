using Ripcord.Domain.Replication;

namespace Ripcord.Tests.Replication;

/// Everything the WMI adapter would otherwise have to decide. It cannot be run off Windows,
/// so each of these lives in the Domain where a test can reach it.
public class CimTranslationTests
{
    /// The host instance is excluded on InstallDate, which is documented Null for the
    /// management operating system — not on Caption, which is localized and would match
    /// nothing on a non-English Windows.
    [Fact]
    public void Only_an_instance_with_an_install_date_is_a_virtual_machine()
    {
        Assert.True(CimTranslation.IsVirtualMachine(new DateTime(2024, 1, 1)));
        Assert.False(CimTranslation.IsVirtualMachine(null));
    }

    /// Extended replication gives a VM two relationships. Reading the wrong one reports the
    /// extended replica's numbers as if they were the primary's.
    [Theory]
    [InlineData(@"Microsoft:A1B2C3D4-0000-1111-2222-333344445555\HVR\0", true)]
    [InlineData(@"Microsoft:A1B2C3D4-0000-1111-2222-333344445555\HVR\1", false)]
    [InlineData(@"Microsoft:A1B2C3D4-0000-1111-2222-333344445555", true)]
    [InlineData(null, true)]
    public void The_primary_relationship_is_told_apart_by_its_instance_id(
        string? instanceId, bool expected)
    {
        Assert.Equal(expected, CimTranslation.IsPrimaryRelationship(instanceId));
    }

    [Fact]
    public void A_vm_with_no_element_name_still_gets_a_name_to_render()
    {
        Assert.Equal("(unnamed)", CimTranslation.VmName(null));
        Assert.Equal("(unnamed)", CimTranslation.VmName("   "));
        Assert.Equal("VM-DC-01", CimTranslation.VmName("VM-DC-01"));
    }

    /// A name read from the local host is printed on the same terminal as the peer's, so it
    /// is cleaned on the same terms.
    [Fact]
    public void A_control_character_in_a_local_vm_name_does_not_reach_the_console() =>
        Assert.Equal("VM-DC-01?[2K", CimTranslation.VmName("VM-DC-01\u001b[2K"));

    /// MI hands back CIM_DATETIME as a DateTime whose kind it sets. Utc and Local convert
    /// themselves; Unspecified is the host's own wall clock, since the value came from that
    /// host's Hyper-V. Assumed, not verified — see V22.
    [Fact]
    public void A_utc_timestamp_is_carried_through_unchanged()
    {
        DateTime utc = new(2026, 9, 12, 14, 0, 0, DateTimeKind.Utc);

        Assert.Equal(
            new DateTimeOffset(utc), CimTranslation.Instant(utc));
    }

    [Fact]
    public void An_unspecified_timestamp_is_read_as_the_hosts_local_time()
    {
        DateTime unspecified = new(2026, 9, 12, 14, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(
            new DateTimeOffset(DateTime.SpecifyKind(unspecified, DateTimeKind.Local)),
            CimTranslation.Instant(unspecified));
    }

    [Fact]
    public void A_missing_timestamp_stays_missing()
    {
        Assert.Null(CimTranslation.Instant(null));
    }
}
