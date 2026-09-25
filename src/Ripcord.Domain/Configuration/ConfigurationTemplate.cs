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
        ["schema_version", "node", "peer", "replication", "storage", "vms", "updates"];

    public static string Render(ConfigurationDraft draft, string? previous = null)
    {
        ArgumentNullException.ThrowIfNull(draft);

        StringBuilder text = new();
        IReadOnlyList<YamlSection> before = YamlSections.Split(previous);

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
        Trailer(text, before, "node");
        text.AppendLine();
        text.AppendLine("peer:");
        text.AppendLine("  # The other host of the pair, and the address it answers on. Not a");
        text.AppendLine("  # name: it is compared against where a connection came from.");
        text.AppendLine($"  hostname: {draft.PeerHostname}");
        text.AppendLine($"  address: {draft.PeerAddress}");
        text.AppendLine($"  offline_after_sec: {Number(draft.PeerOfflineAfterSec)}");
        Trailer(text, before, "peer");
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
        Trailer(text, before, "replication");

        text.AppendLine();
        text.AppendLine("storage:");
        text.AppendLine($"  data_volume: {Quoted(draft.DataVolume)}");
        text.AppendLine($"  free_space_warning_gb: {Number(draft.FreeSpaceWarningGb)}");

        if (draft.Carried.CheckBitlockerAutounlock is { } bitlocker)
        {
            text.AppendLine($"  check_bitlocker_autounlock: {Flag(bitlocker)}");
        }

        Trailer(text, before, "storage");

        text.AppendLine();
        text.AppendLine("# Every VM that matters. P1 fails over first, and 'check' makes sure the");
        text.AppendLine("# DR host can start every P1 at once. P2 is everything that can wait.");
        if (draft.Vms.Count == 0)
        {
            text.AppendLine("# No VM on this host when this was written. Re-run ripcord init.");
            text.AppendLine("vms: []");
        }
        else
        {
            text.AppendLine("vms:");
        }

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

        text.AppendLine();
        text.AppendLine("updates:");
        text.AppendLine("  # Whether `ripcord check-update` may ask GitHub for a newer release.");
        text.AppendLine("  # It needs outbound access, which these hosts are meant not to have.");
        text.AppendLine($"  check: {Flag(draft.CheckUpdates)}");

        if (draft.Carried.InstallUpdates)
        {
            text.AppendLine("  # Whether `ripcord update` may replace this binary. Never asked.");
            text.AppendLine("  install: true");
        }

        Trailer(text, before, "updates");

        foreach (YamlSection section in YamlSections.Except(before, Owned))
        {
            text.AppendLine();

            foreach (string line in Unpadded(WithoutUpdatesExample(Carried(section, draft))))
            {
                text.AppendLine(line);
            }
        }

        if (Unpadded(WithoutUpdatesExample(YamlSections.Epilogue(previous))) is { Count: > 0 } epilogue)
        {
            text.AppendLine();

            foreach (string line in epilogue)
            {
                text.AppendLine(line);
            }
        }

        return text.ToString();
    }

    /// The samples close with a commented-out `updates` example. Once the section is written for
    /// real, that paragraph reads as a second block contradicting the first, so it goes — only
    /// when every line of it is a column-0 comment, so a real key glued to it is never taken.
    private static List<string> WithoutUpdatesExample(IReadOnlyList<string> lines)
    {
        List<string> kept = [];
        int index = 0;

        while (index < lines.Count)
        {
            int start = index;

            while (index < lines.Count && lines[index].Length == 0)
            {
                index++;
            }

            int text = index;

            while (index < lines.Count && lines[index].Length > 0)
            {
                index++;
            }

            List<string> paragraph = [.. lines.Skip(text).Take(index - text)];

            if (!(paragraph.All(line => line[0] == '#')
                && paragraph.Any(line => line.TrimEnd() == "# updates:")))
            {
                kept.AddRange(lines.Skip(start).Take(index - start));
            }
        }

        return kept;
    }

    /// The acknowledgements `Render` took out of `checks` because their VM is no longer declared.
    public static IReadOnlyList<string> DroppedAcknowledgements(
        ConfigurationDraft draft, string? previous)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return YamlSections.Split(previous).FirstOrDefault(section => section.Key == "checks")
            is { } checks
            ? Pruned(checks, draft).DroppedVms
            : [];
    }

    /// A carried section keeps the blank lines at its edges, and a blank is written between
    /// sections here as well: without this the gap grows by one on every re-run.
    private static List<string> Unpadded(List<string> lines)
    {
        int start = 0;
        int end = lines.Count;

        while (start < end && lines[start].Length == 0)
        {
            start++;
        }

        while (end > start && lines[end - 1].Length == 0)
        {
            end--;
        }

        return lines.GetRange(start, end - start);
    }

    private static IReadOnlyList<string> Carried(YamlSection section, ConfigurationDraft draft) =>
        section.Key == "checks" ? Pruned(section, draft).Lines : section.Lines;

    private static AcknowledgementPruning.Pruned Pruned(YamlSection checks, ConfigurationDraft draft) =>
        AcknowledgementPruning.Prune(checks.Lines, [.. draft.Vms.Select(vm => vm.Name)]);

    /// Not for `vms`: a comment closing that list is about one VM, which may be the one dropped.
    private static void Trailer(StringBuilder text, IReadOnlyList<YamlSection> previous, string key)
    {
        foreach (string line in previous.FirstOrDefault(section => section.Key == key)?.Trailer ?? [])
        {
            text.AppendLine(line);
        }
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
