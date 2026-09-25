using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Configuration;

public class AcknowledgementPruningTests
{
    private static readonly string[] Checks =
    [
        "checks:",
        "  acknowledgements:",
        "    # Seen and accepted.",
        "    - rule: passthrough-disk-on-replicated-vm",
        "      vm: VM-BACKUP-01",
        "      reason: \"by design\"",
        "      expires: 2027-09-01",
        "    - rule: guest-os-support",
        "      vm: 'VM-DC-01'   # quoted",
        "      expires: 2027-01-01",
    ];

    [Fact]
    public void An_entry_for_a_vm_no_longer_declared_is_taken_out_and_named()
    {
        AcknowledgementPruning.Pruned pruned = AcknowledgementPruning.Prune(Checks, ["vm-dc-01"]);

        Assert.Equal(["VM-BACKUP-01"], pruned.DroppedVms);
        Assert.Equal(
            [
                "checks:",
                "  acknowledgements:",
                "    # Seen and accepted.",
                "    - rule: guest-os-support",
                "      vm: 'VM-DC-01'   # quoted",
                "      expires: 2027-01-01",
            ],
            pruned.Lines);
    }

    [Fact]
    public void Taking_out_every_entry_leaves_an_empty_list_not_a_null()
    {
        AcknowledgementPruning.Pruned pruned = AcknowledgementPruning.Prune(Checks, []);

        Assert.Equal(["VM-BACKUP-01", "VM-DC-01"], pruned.DroppedVms);
        Assert.Equal("  acknowledgements: []", pruned.Lines[1]);
        Assert.DoesNotContain(pruned.Lines, line => line.Contains("rule:", StringComparison.Ordinal));
    }

    [Fact]
    public void Entries_for_declared_vms_and_without_a_vm_are_left_exactly_as_they_were()
    {
        string[] checks = [.. Checks, "    - rule: host-wide", "      expires: 2027-01-01"];

        AcknowledgementPruning.Pruned pruned =
            AcknowledgementPruning.Prune(checks, ["VM-BACKUP-01", "VM-DC-01"]);

        Assert.Empty(pruned.DroppedVms);
        Assert.Equal(checks, pruned.Lines);
    }

    [Fact]
    public void A_flow_style_list_is_not_recognised_and_left_alone()
    {
        string[] checks = ["checks:", "  acknowledgements: [{ rule: x, vm: VM-GONE }]"];

        Assert.Empty(AcknowledgementPruning.Prune(checks, []).DroppedVms);
    }

    [Fact]
    public void What_follows_the_list_is_kept()
    {
        string[] checks = [.. Checks, "  later_key: true"];

        Assert.Equal("  later_key: true", AcknowledgementPruning.Prune(checks, ["VM-DC-01"]).Lines[^1]);
    }
}
