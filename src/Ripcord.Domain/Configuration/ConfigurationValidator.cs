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
        string? nodeHostname = ValidateNode(document.Node, machineName, errors);
        PeerSettings? peer = ValidatePeer(document.Peer, nodeHostname, errors);
        IReadOnlyList<VmSettings> vms = ValidateVms(document.Vms, errors);

        return errors.Count > 0 || nodeHostname is null || peer is null
            ? ConfigurationValidation.Invalid(errors)
            : ConfigurationValidation.Valid(
                new RipcordConfiguration(new NodeSettings(nodeHostname), peer, vms));
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
    private static string? ValidateNode(
        NodeDocument? node, string machineName, List<ConfigurationError> errors)
    {
        if (node is null)
        {
            errors.Add(new ConfigurationError("node", "required section"));
            return null;
        }

        if (!Required(node.Hostname, "node.hostname", errors, out string hostname))
        {
            return null;
        }

        if (!SameHost(hostname, machineName))
        {
            errors.Add(new ConfigurationError(
                "node.hostname",
                $"declares '{hostname}' but this machine is '{machineName}'; "
                + "this looks like the other host's configuration"));
            return null;
        }

        return hostname;
    }

    private static PeerSettings? ValidatePeer(
        PeerDocument? peer, string? nodeHostname, List<ConfigurationError> errors)
    {
        if (peer is null)
        {
            errors.Add(new ConfigurationError("peer", "required section"));
            return null;
        }

        bool complete = Required(peer.Hostname, "peer.hostname", errors, out string hostname);

        if (complete && nodeHostname is not null && SameHost(hostname, nodeHostname))
        {
            errors.Add(new ConfigurationError(
                "peer.hostname", "must name the other host, not this one"));
            complete = false;
        }

        complete &= Required(peer.Address, "peer.address", errors, out string address);

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
                    vm.ExpectedStartupRamMb));
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

    /// Windows host names are case-insensitive; a config differing only in case is correct.
    private static bool SameHost(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
