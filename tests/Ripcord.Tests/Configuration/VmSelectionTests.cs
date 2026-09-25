using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Configuration;

/// The VM question's default and its parser, which must agree: Enter on the default gives
/// back the file's VMs still on this host, in the file's order.
public class VmSelectionTests
{
    private static readonly string[] Host = ["VM-DC-01", "VM-APP-01", "VM-BACKUP-01"];

    [Fact]
    public void A_re_run_offers_the_files_vms_as_their_numbers() =>
        Assert.Equal("1,3", VmSelection.Default(Host, ["VM-DC-01", "VM-BACKUP-01"]));

    [Fact]
    public void The_default_keeps_the_files_order() =>
        Assert.Equal("3,1", VmSelection.Default(Host, ["VM-BACKUP-01", "VM-DC-01"]));

    [Fact]
    public void Every_vm_in_the_hosts_order_is_all() =>
        Assert.Equal("all", VmSelection.Default(Host, ["vm-dc-01", "VM-APP-01", "VM-BACKUP-01"]));

    [Fact]
    public void Every_vm_in_another_order_stays_numbers() =>
        Assert.Equal("2,1,3", VmSelection.Default(Host, ["VM-APP-01", "VM-DC-01", "VM-BACKUP-01"]));

    [Fact]
    public void A_vm_no_longer_on_the_host_is_not_in_the_default() =>
        Assert.Equal("2", VmSelection.Default(Host, ["VM-GONE-01", "VM-APP-01"]));

    /// Nothing of the file is on the host (the shipped sample, typically): everything is the
    /// usual answer on a host being set up.
    [Fact]
    public void No_seeded_vm_on_the_host_defaults_to_all()
    {
        Assert.Equal("all", VmSelection.Default(Host, ["VM-GONE-01"]));
        Assert.Equal("all", VmSelection.Default(Host, []));
    }

    [Fact]
    public void With_no_list_there_is_no_default() =>
        Assert.Null(VmSelection.Default([], ["VM-DC-01"]));

    [Theory]
    [InlineData("1,VM-APP-01", new[] { "VM-DC-01", "VM-APP-01" })]
    [InlineData("vm-dc-01", new[] { "VM-DC-01" })]
    [InlineData(" 3 , 1 ", new[] { "VM-BACKUP-01", "VM-DC-01" })]
    [InlineData("1,vm-dc-01,1", new[] { "VM-DC-01" })]
    [InlineData("ALL", new[] { "VM-DC-01", "VM-APP-01", "VM-BACKUP-01" })]
    public void Numbers_names_and_all_are_accepted(string typed, string[] expected) =>
        Assert.Equal(new VmChoice(expected, null), VmSelection.Parse(typed, Host));

    [Theory]
    [InlineData("all,1", "'all' stands alone.")]
    [InlineData("7", "there is no VM 7 in the list.")]
    [InlineData("VM-DC-1", "there is no VM VM-DC-1 in the list.")]
    [InlineData(",", "name at least one VM.")]
    public void What_is_not_in_the_list_is_refused(string typed, string refusal) =>
        Assert.Equal(refusal, VmSelection.Parse(typed, Host).Refusal);

    [Fact]
    public void A_number_outside_the_list_can_still_be_a_vms_name() =>
        Assert.Equal(["2019"], VmSelection.Parse("2019", ["VM-DC-01", "2019"]).Names);

    [Fact]
    public void With_no_list_names_are_taken_as_typed_and_all_is_refused()
    {
        Assert.Equal(["VM-DC-01", "VM-X"], VmSelection.Parse("VM-DC-01, VM-X", []).Names);
        Assert.NotNull(VmSelection.Parse("all", []).Refusal);
    }

    [Theory]
    [InlineData("VM-DC-01,VM-BACKUP-01")]
    [InlineData("VM-BACKUP-01,VM-DC-01")]
    [InlineData("VM-GONE-01,VM-APP-01")]
    [InlineData("VM-DC-01,VM-APP-01,VM-BACKUP-01")]
    [InlineData("VM-APP-01,VM-DC-01,VM-BACKUP-01")]
    [InlineData("vm-backup-01,VM-BACKUP-01")]
    public void Enter_on_the_default_reproduces_the_selection(string file)
    {
        string[] seeded = file.Split(',');
        string[] kept = [.. seeded
            .Select(name => Host.FirstOrDefault(vm => string.Equals(vm, name, StringComparison.OrdinalIgnoreCase)))
            .OfType<string>()
            .Distinct()];

        VmChoice choice = VmSelection.Parse(VmSelection.Default(Host, seeded)!, Host);

        Assert.Null(choice.Refusal);
        Assert.Equal(kept, choice.Names);
    }
}
