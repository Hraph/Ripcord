using Ripcord.Domain.Checks;

namespace Ripcord.Domain.Configuration;

/// Turns a document that was read into a configuration that can be trusted, or into the full
/// list of reasons it cannot. Every error is collected: failing on the first one means
/// re-running once per typo, which is not what anyone wants at 3 a.m.
///
/// It answers "can this file be read?" only. Whether reality matches the file — the switch
/// exists, the target has the RAM, the certificate is valid — is `ripcord check`.
public static class ConfigurationValidator
{
    /// Versioned migrations are planned from the start, so the known set is a set, not a max.
    private static readonly int[] KnownSchemaVersions = [1];

    private const int MaxOfflineAfterSec = 86_400;

    /// Bounds that are not opinions: a reserve larger than any host this tool targets, a
    /// frequency longer than a day, a multiplier past which the rule can never fire.
    private const int MaxHostReserveGb = 512;

    private const int MaxFrequencySec = 86_400;

    private const int MaxLagMultiplier = 1_000;

    private const int MaxFreeSpaceWarningGb = 1_000_000;

    public static ConfigurationValidation Validate(
        ConfigurationDocument? document, string machineName)
    {
        if (document is null)
        {
            return ConfigurationValidation.Invalid(
                [new ConfigurationError("", "the configuration file is empty")]);
        }

        List<ConfigurationError> errors = [];

        ValidateSchemaVersion(document.SchemaVersion, errors);
        NodeSettings? node = ValidateNode(document.Node, machineName, errors);
        PeerSettings? peer = ValidatePeer(document.Peer, node?.Hostname, errors);
        ListenerSettings? listener = ValidateListener(document.Listener, errors);
        ReplicationSettings? replication = ValidateReplication(document.Replication, errors);
        StorageSettings? storage = ValidateStorage(document.Storage, errors);
        IReadOnlyList<VmSettings> vms = ValidateVms(document.Vms, errors);

        IReadOnlyList<Acknowledgement> acknowledgements =
            ValidateAcknowledgements(document.Checks, vms, errors);

        return errors.Count > 0
            || node is null
            || peer is null
            || listener is null
            || replication is null
            || storage is null
            ? ConfigurationValidation.Invalid(errors)
            : ConfigurationValidation.Valid(new RipcordConfiguration(
                node, peer, listener, replication, storage, vms, acknowledgements));
    }

    private static void ValidateSchemaVersion(int? version, List<ConfigurationError> errors)
    {
        if (version is null)
        {
            errors.Add(new ConfigurationError("schema_version", "required"));
        }
        else if (!KnownSchemaVersions.Contains(version.Value))
        {
            errors.Add(new ConfigurationError(
                "schema_version",
                $"unknown version {version}; this binary understands "
                + string.Join(", ", KnownSchemaVersions)));
        }
    }

    /// Decision D9: a config copied from one host to the other without swapping the blocks
    /// yields a tool that believes it is on the other side. Blocking, never a warning.
    private static NodeSettings? ValidateNode(
        NodeDocument? node, string machineName, List<ConfigurationError> errors)
    {
        if (node is null)
        {
            errors.Add(new ConfigurationError("node", "required section"));
            return null;
        }

        string? hostname = ValidateHostname(node.Hostname, machineName, errors);

        // Required rather than defaulted: a reserve nobody declared would read as zero, and a
        // zero reserve overstates the target's usable memory — which is how a feasibility
        // calculation approves a failover that cannot boot.
        if (node.HostMemoryReserveGb is not (> 0 and <= MaxHostReserveGb))
        {
            errors.Add(new ConfigurationError(
                "node.host_memory_reserve_gb",
                $"required, between 1 and {MaxHostReserveGb}"));
            return null;
        }

        return hostname is null
            ? null
            : new NodeSettings(hostname, node.HostMemoryReserveGb.Value);
    }

    /// Decision D9, kept apart from the reserve so a wrong host name and a missing reserve are
    /// both reported in one pass.
    private static string? ValidateHostname(
        string? declared, string machineName, List<ConfigurationError> errors)
    {
        if (!Required(declared, "node.hostname", errors, out string hostname))
        {
            return null;
        }

        if (!SameName(hostname, machineName))
        {
            errors.Add(new ConfigurationError(
                "node.hostname",
                $"declares '{hostname}' but this machine is '{machineName}'; "
                + "this looks like the other host's configuration"));
            return null;
        }

        return hostname;
    }

    /// Everything `check` compares reality against. A missing field here disables a critical
    /// rule, so none of them is optional — except the health threshold, which has a stated
    /// default rather than a silent one.
    private static ReplicationSettings? ValidateReplication(
        ReplicationDocument? replication, List<ConfigurationError> errors)
    {
        if (replication is null)
        {
            errors.Add(new ConfigurationError("replication", "required section"));
            return null;
        }

        bool complete = true;
        ExpectedRole role = default;

        // Matched by name, never by ordinal: the same trap as vms[].priority, and an ordinal
        // read as the wrong role would invert the whole report.
        if (!Enum.GetNames<ExpectedRole>().Contains(
                replication.ExpectedRole?.Trim() ?? "", StringComparer.OrdinalIgnoreCase)
            || !Enum.TryParse(replication.ExpectedRole, ignoreCase: true, out role))
        {
            errors.Add(new ConfigurationError(
                "replication.expected_role",
                "required, one of " + string.Join(", ", Enum.GetNames<ExpectedRole>())));
            complete = false;
        }

        complete &= Required(
            replication.ExpectedSwitchName,
            "replication.expected_switch_name",
            errors,
            out string switchName);

        if (replication.ExpectedFrequencySec is not (> 0 and <= MaxFrequencySec))
        {
            errors.Add(new ConfigurationError(
                "replication.expected_frequency_sec",
                $"required, between 1 and {MaxFrequencySec}"));
            complete = false;
        }

        if (replication.LagWarningMultiplier is not (> 0 and <= MaxLagMultiplier))
        {
            errors.Add(new ConfigurationError(
                "replication.lag_warning_multiplier",
                $"required, between 1 and {MaxLagMultiplier}"));
            complete = false;
        }

        if (replication.HealthWarningAfterSec is not (null or (> 0 and <= MaxFrequencySec)))
        {
            errors.Add(new ConfigurationError(
                "replication.health_warning_after_sec",
                $"must be between 1 and {MaxFrequencySec} when present"));
            complete = false;
        }

        complete &= TestFailoverSwitch(replication, switchName, errors, out string? testSwitch);

        return complete
            ? new ReplicationSettings(
                role,
                switchName,
                TimeSpan.FromSeconds(replication.ExpectedFrequencySec!.Value),
                replication.LagWarningMultiplier!.Value,
                replication.HealthWarningAfterSec is { } seconds
                    ? TimeSpan.FromSeconds(seconds)
                    : ReplicationSettings.DefaultHealthWarningAfter,
                testSwitch)
            : null;
    }

    /// Optional (decision D12): absent means a test VM is started with every adapter
    /// disconnected, which is the safe default. Present and useless is refused instead —
    /// blank reads as absent while the operator believes they configured something, and the
    /// production switch would satisfy the isolation rule by doing what it forbids.
    private static bool TestFailoverSwitch(
        ReplicationDocument replication,
        string expectedSwitchName,
        List<ConfigurationError> errors,
        out string? testSwitch)
    {
        const string Path = "replication.test_failover_switch";

        testSwitch = null;

        if (replication.TestFailoverSwitch is not { } declared)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(declared))
        {
            errors.Add(new ConfigurationError(
                Path, "must name a switch when present; remove the key to disconnect instead"));
            return false;
        }

        if (string.Equals(
                declared.Trim(), expectedSwitchName, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(new ConfigurationError(
                Path,
                "must differ from replication.expected_switch_name: a test VM on the "
                    + "production switch is the duplicate identity this setting prevents"));
            return false;
        }

        testSwitch = declared.Trim();
        return true;
    }

    private static StorageSettings? ValidateStorage(
        StorageDocument? storage, List<ConfigurationError> errors)
    {
        if (storage is null)
        {
            errors.Add(new ConfigurationError("storage", "required section"));
            return null;
        }

        bool complete = true;
        string? volume = NormalisedDrive(storage.DataVolume);

        // Matched against what Windows reports and printed in the remedy command, so it has
        // to be a drive rather than any string a path could be mistaken for.
        if (volume is null)
        {
            errors.Add(new ConfigurationError(
                "storage.data_volume", "required, a drive such as 'D:'"));
            complete = false;
        }

        if (storage.FreeSpaceWarningGb is not (> 0 and <= MaxFreeSpaceWarningGb))
        {
            errors.Add(new ConfigurationError(
                "storage.free_space_warning_gb",
                $"required, between 1 and {MaxFreeSpaceWarningGb}"));
            complete = false;
        }

        return complete
            ? new StorageSettings(
                volume!, storage.FreeSpaceWarningGb!.Value, storage.CheckBitlockerAutounlock)
            : null;
    }

    /// "D:", "d:" and "D:\" all name the same volume; anything else is not a drive.
    private static string? NormalisedDrive(string? value)
    {
        string trimmed = (value ?? "").Trim().TrimEnd('\\', '/');

        return trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':'
            ? trimmed.ToUpperInvariant()
            : null;
    }

    /// An acknowledgement that names nothing real suppresses nothing while telling the
    /// operator it does — the one failure mode worse than the finding it was meant to hide.
    private static List<Acknowledgement> ValidateAcknowledgements(
        ChecksDocument? checks,
        IReadOnlyList<VmSettings> vms,
        List<ConfigurationError> errors)
    {
        if (checks?.Acknowledgements is not { } entries)
        {
            return [];
        }

        List<Acknowledgement> acknowledgements = [];

        for (int index = 0; index < entries.Count; index++)
        {
            AcknowledgementDocument entry = entries[index];
            string path = $"checks.acknowledgements[{index}]";
            bool complete = true;

            if (CheckRules.ById(entry.Rule?.Trim()) is not { } rule)
            {
                errors.Add(new ConfigurationError(
                    $"{path}.rule", $"'{entry.Rule}' is not a rule this binary knows"));
                complete = false;
            }
            else if (!rule.Acknowledgeable)
            {
                errors.Add(new ConfigurationError(
                    $"{path}.rule",
                    $"'{rule.Id}' can never be acknowledged: it means the service would not "
                    + "come back at all"));
                complete = false;
            }

            string? vmName = entry.Vm?.Trim() is { Length: > 0 } named ? named : null;

            if (vmName is not null
                && !vms.Any(vm => SameName(vm.Name, vmName)))
            {
                errors.Add(new ConfigurationError(
                    $"{path}.vm", $"'{vmName}' is not one of the declared VMs"));
                complete = false;
            }

            complete &= Required(entry.Reason, $"{path}.reason", errors, out string reason);

            // Mandatory (decision D20). A permanent acknowledgement outlives the reason it
            // was accepted, and nobody ever revisits it.
            if (entry.Expires is not { } expires)
            {
                errors.Add(new ConfigurationError(
                    $"{path}.expires", "required, the date this acknowledgement lapses"));
                complete = false;
            }

            if (!complete)
            {
                continue;
            }

            Acknowledgement acknowledgement = new(
                entry.Rule!.Trim(), vmName, reason, AsUtcDate(entry.Expires!.Value));

            // Two entries for the same rule and scope means one of them is dead text, and
            // nobody finds out which was meant to win.
            if (acknowledgements.Any(existing =>
                existing.Covers(acknowledgement.RuleId, acknowledgement.VmName)))
            {
                errors.Add(new ConfigurationError(
                    $"{path}.rule", $"'{acknowledgement.RuleId}' is acknowledged twice"));
                continue;
            }

            acknowledgements.Add(acknowledgement);
        }

        return acknowledgements;
    }

    /// A YAML date carries no time zone. Read as midnight UTC so both hosts agree on the day
    /// an acknowledgement lapses whatever their local offset.
    private static DateTimeOffset AsUtcDate(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static PeerSettings? ValidatePeer(
        PeerDocument? peer, string? nodeHostname, List<ConfigurationError> errors)
    {
        if (peer is null)
        {
            errors.Add(new ConfigurationError("peer", "required section"));
            return null;
        }

        bool complete = Required(peer.Hostname, "peer.hostname", errors, out string hostname);

        if (complete && nodeHostname is not null && SameName(hostname, nodeHostname))
        {
            errors.Add(new ConfigurationError(
                "peer.hostname", "must name the other host, not this one"));
            complete = false;
        }

        complete &= Required(peer.Address, "peer.address", errors, out string address);

        // Not merely non-empty: it is compared against the address a connection came from, and
        // it lands in a firewall rule. A host name would refuse every connection, and the word
        // "any" would open the port to everyone.
        if (complete && !System.Net.IPAddress.TryParse(address, out _))
        {
            errors.Add(new ConfigurationError(
                "peer.address", $"'{address}' is not an IP address"));
            complete = false;
        }

        if (peer.OfflineAfterSec is not (> 0 and <= MaxOfflineAfterSec))
        {
            errors.Add(new ConfigurationError(
                "peer.offline_after_sec", $"required, between 1 and {MaxOfflineAfterSec}"));
            complete = false;
        }

        return complete
            ? new PeerSettings(
                hostname, address, TimeSpan.FromSeconds(peer.OfflineAfterSec!.Value))
            : null;
    }

    /// No block at all is the documented off switch: the node degrades to the local-only
    /// view. A block that is present and enabled has to be complete, because a listener
    /// started without both thumbprints is a listener without mutual authentication.
    private static ListenerSettings? ValidateListener(
        ListenerDocument? listener, List<ConfigurationError> errors)
    {
        if (listener is null)
        {
            return ListenerSettings.Disabled();
        }

        bool complete = true;
        int port = listener.Port ?? ListenerSettings.DefaultPort;

        if (port is <= 0 or > 65_535)
        {
            errors.Add(new ConfigurationError("listener.port", "must be between 1 and 65535"));
            complete = false;
        }

        string? local = ValidateThumbprint(
            listener.LocalCertificateThumbprint,
            "listener.local_certificate_thumbprint",
            listener.Enabled,
            errors,
            ref complete);

        string? peer = ValidateThumbprint(
            listener.PeerCertificateThumbprint,
            "listener.peer_certificate_thumbprint",
            listener.Enabled,
            errors,
            ref complete);

        // The same certificate on both ends would authenticate a host to itself.
        if (local is not null && local == peer)
        {
            errors.Add(new ConfigurationError(
                "listener.peer_certificate_thumbprint",
                "must identify the other host's certificate, not this one's"));
            complete = false;
        }

        string snapshotPath = string.IsNullOrWhiteSpace(listener.SnapshotPath)
            ? ListenerSettings.DefaultSnapshotPath
            : listener.SnapshotPath.Trim();

        // It is interpolated into a quoted icacls argument; a quote inside it would break out
        // of that quoting.
        if (snapshotPath.Contains('"', StringComparison.Ordinal))
        {
            errors.Add(new ConfigurationError(
                "listener.snapshot_path", "must not contain a quote character"));
            complete = false;
        }

        return complete
            ? new ListenerSettings(listener.Enabled, port, local, peer, snapshotPath)
            : null;
    }

    /// Windows tooling copies thumbprints with spaces and in either case; both paste forms
    /// name the same certificate, so they are normalised rather than refused.
    private static string? ValidateThumbprint(
        string? value,
        string path,
        bool required,
        List<ConfigurationError> errors,
        ref bool complete)
    {
        string normalised = (value ?? "").Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

        if (normalised.Length == 0)
        {
            if (required)
            {
                errors.Add(new ConfigurationError(path, "required when the listener is enabled"));
                complete = false;
            }

            return null;
        }

        if (normalised.Length != 40 || !normalised.All(Uri.IsHexDigit))
        {
            errors.Add(new ConfigurationError(path, "must be 40 hexadecimal characters"));
            complete = false;
            return null;
        }

        return normalised;
    }

    private static List<VmSettings> ValidateVms(
        List<VmDocument>? vms, List<ConfigurationError> errors)
    {
        if (vms is null || vms.Count == 0)
        {
            errors.Add(new ConfigurationError("vms", "required, at least one VM"));
            return [];
        }

        List<VmSettings> settings = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < vms.Count; index++)
        {
            VmDocument vm = vms[index];
            string path = $"vms[{index}]";
            bool complete = Required(vm.Name, $"{path}.name", errors, out string name);

            // A VM declared twice means one block is silently ignored, and nobody finds out
            // which until a failover skips a machine.
            if (complete && !names.Add(name))
            {
                errors.Add(new ConfigurationError($"{path}.name", $"'{name}' is declared twice"));
                complete = false;
            }

            // Matched by name, never by ordinal: Enum.TryParse alone would read "1" as P2,
            // handing the second tier to an operator who meant the first.
            VmPriority priority = default;

            if (!IsPriorityName(vm.Priority)
                || !Enum.TryParse(vm.Priority, ignoreCase: true, out priority))
            {
                errors.Add(new ConfigurationError(
                    $"{path}.priority",
                    "required, one of " + string.Join(", ", Enum.GetNames<VmPriority>())));
                complete = false;
            }

            // Optional and unused at this milestone (decision D5), but a present absurd value
            // is still a typo worth reporting.
            if (vm.ExpectedStartupRamMb is <= 0)
            {
                errors.Add(new ConfigurationError(
                    $"{path}.expected_startup_ram_mb", "must be positive when present"));
                complete = false;
            }

            if (complete)
            {
                settings.Add(new VmSettings(
                    name,
                    priority,
                    vm.IsDomainController,
                    vm.HasPassthroughDisk,
                    vm.ExpectedStartupRamMb,
                    vm.GuestOsSupportEnds is { } supportEnds ? AsUtcDate(supportEnds) : null));
            }
        }

        return settings;
    }

    private static bool IsPriorityName(string? value) =>
        Enum.GetNames<VmPriority>()
            .Contains(value?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);

    private static bool Required(
        string? value, string path, List<ConfigurationError> errors, out string trimmed)
    {
        trimmed = value?.Trim() ?? "";

        if (trimmed.Length > 0)
        {
            return true;
        }

        errors.Add(new ConfigurationError(path, "required"));
        return false;
    }

    /// Windows host names and VM names are both case-insensitive, so a configuration
    /// differing only in case is correct rather than a mismatch.
    private static bool SameName(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
