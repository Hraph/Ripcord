using System.Globalization;
using System.Text;

namespace Ripcord.Domain.Configuration;

/// A draft, and whatever the previous file said about everything else, as one `ripcord.yaml`.
///
/// The comments are part of the output, not decoration. This file is opened a year later by
/// somebody who did not write it, and the line above a value is what stops them guessing.
public static class ConfigurationTemplate
{
    /// The sections the interview asks about, and therefore the ones rewritten. Everything
    /// else in the previous file is carried across untouched.
    public static readonly string[] Owned =
        ["schema_version", "node", "peer", "replication", "storage", "vms"];

    public static string Render(ConfigurationDraft draft, string? previous = null)
    {
        ArgumentNullException.ThrowIfNull(draft);

        StringBuilder text = new();

        text.AppendLine("# Written by `ripcord init`. Re-run it to change any of this: every");
        text.AppendLine("# question arrives answered with what is already here.");
        text.AppendLine();
        text.AppendLine("schema_version: 1");
        text.AppendLine();
        text.AppendLine("node:");
        text.AppendLine($"  hostname: {draft.NodeHostname}");
        text.AppendLine("  # Held back from the failover target's capacity check, for the");
        text.AppendLine("  # management OS itself.");
        text.AppendLine($"  host_memory_reserve_gb: {Number(draft.HostMemoryReserveGb)}");
        text.AppendLine();
        text.AppendLine("peer:");
        text.AppendLine("  # The other host of the pair, and the address it answers on. Not a");
        text.AppendLine("  # name: it is compared against where a connection came from.");
        text.AppendLine($"  hostname: {draft.PeerHostname}");
        text.AppendLine($"  address: {draft.PeerAddress}");
        text.AppendLine($"  offline_after_sec: {Number(draft.PeerOfflineAfterSec)}");
        text.AppendLine();
        text.AppendLine("replication:");
        text.AppendLine("  # Which side this host normally is. The pair's two files are mirror");
        text.AppendLine("  # images, and nothing observable says which way round it should run.");
        text.AppendLine($"  expected_role: {Role(draft.Role)}");
        text.AppendLine("  # The switch the replicas are expected to be attached to on the");
        text.AppendLine("  # failover target.");
        text.AppendLine($"  expected_switch_name: {Quoted(draft.SwitchName)}");
        text.AppendLine($"  expected_frequency_sec: {Number(draft.FrequencySec)}");
        text.AppendLine($"  lag_warning_multiplier: {Number(draft.LagMultiplier)}");

        // Never asked, never invented: whatever the file said, given back. Written after the
        // answered keys so the part the interview owns stays at the top of the section.
        Carried(text, draft.Carried);

        text.AppendLine();
        text.AppendLine("storage:");
        text.AppendLine($"  data_volume: {Quoted(draft.DataVolume)}");
        text.AppendLine($"  free_space_warning_gb: {Number(draft.FreeSpaceWarningGb)}");

        if (draft.Carried.CheckBitlockerAutounlock is { } bitlocker)
        {
            text.AppendLine($"  check_bitlocker_autounlock: {Flag(bitlocker)}");
        }

        text.AppendLine();
        text.AppendLine("# Every VM that matters, in failover order. P1 comes back first, and a");
        text.AppendLine("# P1 running on both hosts at once halts every mutating command.");
        text.AppendLine("vms:");

        foreach (DraftVm vm in draft.Vms)
        {
            text.AppendLine($"  - name: {Quoted(vm.Name)}");
            text.AppendLine($"    priority: {vm.Priority}");
            text.AppendLine($"    is_domain_controller: {Flag(vm.IsDomainController)}");

            if (vm.HasPassthroughDisk)
            {
                text.AppendLine("    has_passthrough_disk: true");
            }

            if (vm.ExpectedStartupRamMb is { } ram)
            {
                text.AppendLine($"    expected_startup_ram_mb: {Number(ram)}");
            }

            if (vm.GuestOsSupportEnds is { } ends)
            {
                text.AppendLine($"    guest_os_support_ends: {ends:yyyy-MM-dd}");
            }

            if (vm.Failover is { Length: > 0 } policy)
            {
                text.AppendLine($"    failover: {policy}");
            }
        }

        foreach (YamlSection section in YamlSections.Except(YamlSections.Split(previous), Owned))
        {
            text.AppendLine();

            foreach (string line in section.Lines)
            {
                text.AppendLine(line);
            }
        }

        return text.ToString();
    }

    /// What was kept from the previous file, for the operator to read before saying yes. Named
    /// rather than counted: "three sections carried over" is not something anybody can check.
    public static IReadOnlyList<string> CarriedOver(string? previous) =>
        [.. YamlSections.Except(YamlSections.Split(previous), Owned).Select(section => section.Key)];

    private static void Carried(StringBuilder text, CarriedSettings carried)
    {
        if (carried.HealthWarningAfterSec is { } health)
        {
            text.AppendLine($"  health_warning_after_sec: {Number(health)}");
        }

        if (carried.TestFailoverSwitch is { Length: > 0 } switchName)
        {
            text.AppendLine($"  test_failover_switch: {Quoted(switchName)}");
        }

        if (carried.TestFailoverOrphanAfterHours is { } orphan)
        {
            text.AppendLine($"  test_failover_orphan_after_hours: {Number(orphan)}");
        }

        if (carried.UnattendedTestFailoverVms is { Count: > 0 } unattended)
        {
            // The one authorisation in the file: these VMs may be test-failed-over with nobody
            // typing the node name. Dropping it on a rewrite would silently revoke it.
            text.AppendLine("  unattended_test_failover_vms:");

            foreach (string name in unattended)
            {
                text.AppendLine($"    - {Quoted(name)}");
            }
        }
    }

    private static string Flag(bool value) => value ? "true" : "false";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// Written as the validator reads it: by name, never as an ordinal.
    private static string Role(ExpectedRole role) =>
        role == ExpectedRole.Replica ? "replica" : "primary";

    /// A VM or switch name is somebody else's string. Quoted so a colon or a leading digit in
    /// it cannot turn one line of this file into something else.
    private static string Quoted(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
