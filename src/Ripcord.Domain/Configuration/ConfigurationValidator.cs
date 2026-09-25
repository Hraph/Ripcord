using Ripcord.Domain.Alerting;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Dashboard;
using Ripcord.Domain.Updates;

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

    /// A year. Past that the rule is switched off, and a rule switched off by a large number
    /// is harder to notice than one switched off by name.
    private const int MaxOrphanHours = 8_760;

    private const int MaxFreeSpaceWarningGb = 1_000_000;

    /// A year, same reasoning as the orphan window: a threshold long enough to switch the
    /// repeat off should be switched off by name, not by a large number.
    private const int MaxRepeatAfterHours = 8_760;

    /// Submission, not 25: the relays these hosts would use take mail on 587 with STARTTLS.
    private const int DefaultSmtpPort = 587;

    /// `configurationPath` is only there to place the snapshot when the file does not say
    /// where it goes: the default sits beside the configuration, and nothing else in here
    /// depends on where that is.
    public static ConfigurationValidation Validate(
        ConfigurationDocument? document, string machineName, string? configurationPath = null)
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
        ListenerSettings? listener =
            ValidateListener(document.Listener, configurationPath, errors);
        ReplicationSettings? replication = ValidateReplication(document.Replication, errors);
        StorageSettings? storage = ValidateStorage(document.Storage, errors);
        AlertingSettings? alerting = ValidateAlerting(document.Alerting, errors);

        UpdateSettings? updates = ValidateUpdates(document.Updates, errors);
        DashboardSettings? dashboard = ValidateDashboard(document.Dashboard, listener, errors);
        IReadOnlyList<VmSettings> vms = ValidateVms(document.Vms, errors);

        IReadOnlyList<Acknowledgement> acknowledgements =
            ValidateAcknowledgements(document.Checks, vms, errors);

        // Validated here rather than inside the replication section: the names are checked
        // against the VM list, which is not known until it has been validated itself.
        replication = ValidateUnattended(document.Replication, replication, vms, errors);

        return errors.Count > 0
            || node is null
            || peer is null
            || listener is null
            || replication is null
            || storage is null
            || alerting is null
            || dashboard is null
            || updates is null
            ? ConfigurationValidation.Invalid(errors)
            : ConfigurationValidation.Valid(new RipcordConfiguration(
                node, peer, listener, replication, storage, alerting, updates, dashboard,
                vms, acknowledgements));
    }

    /// Authorising a VM to be tested with no human present is the one place the typed
    /// confirmation is waived, so a name that matches nothing is refused rather than ignored:
    /// an operator who believes a VM is authorised when it is not will find the scheduled
    /// task silently doing nothing.
    private static ReplicationSettings? ValidateUnattended(
        ReplicationDocument? document,
        ReplicationSettings? replication,
        IReadOnlyList<VmSettings> vms,
        List<ConfigurationError> errors)
    {
        const string Path = "replication.unattended_test_failover_vms";

        if (replication is null || document?.UnattendedTestFailoverVms is not { } declared)
        {
            return replication;
        }

        List<string> authorised = [];

        foreach (string entry in declared)
        {
            string name = entry?.Trim() ?? "";

            if (name.Length == 0)
            {
                errors.Add(new ConfigurationError(Path, "an entry names no VM"));
                continue;
            }

            if (!vms.Any(vm => string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add(new ConfigurationError(
                    Path, $"'{name}' is not a VM in this configuration"));
                continue;
            }

            authorised.Add(name);
        }

        return replication with { UnattendedTestFailoverVmsOrNone = authorised };
    }

    /// The block is checked whether or not it is switched on: switching alerting on must not
    /// be the moment the typos in the relay address surface.
    private static AlertingSettings? ValidateAlerting(
        AlertingDocument? alerting, List<ConfigurationError> errors)
    {
        if (alerting is null)
        {
            return AlertingSettings.Disabled();
        }

        bool complete = true;

        int hours = alerting.RepeatAfterHours ?? (int)AlertingSettings.DefaultRepeatAfter.TotalHours;

        if (hours is <= 0 or > MaxRepeatAfterHours)
        {
            errors.Add(new ConfigurationError(
                "alerting.repeat_after_hours",
                $"must be between 1 and {MaxRepeatAfterHours}"));
            complete = false;
        }

        QuietHours? quiet = null;

        if (!string.IsNullOrWhiteSpace(alerting.QuietHours)
            && !QuietHours.TryParse(alerting.QuietHours, out quiet))
        {
            errors.Add(new ConfigurationError(
                "alerting.quiet_hours", "must read as HH:mm-HH:mm, for instance 22:00-07:00"));
            complete = false;
        }

        SmtpSettings? smtp = ValidateSmtp(alerting.Smtp, errors, ref complete);
        WebhookSettings? webhook = ValidateWebhook(alerting.Webhook, errors, ref complete);

        // Enabled with nowhere to send is the failure nobody sees until the night it matters:
        // every run decides to notify, and nothing ever arrives. Judged on the blocks that
        // are written rather than on the ones that validated, so a typo in the relay address
        // is reported once instead of twice.
        if (alerting.Enabled && alerting.Smtp is null && alerting.Webhook is null)
        {
            errors.Add(new ConfigurationError(
                "alerting", "enabled, but neither an smtp nor a webhook block says where to send"));
            complete = false;
        }

        return complete
            ? new AlertingSettings(alerting.Enabled, TimeSpan.FromHours(hours), quiet, smtp, webhook)
            : null;
    }

    private static SmtpSettings? ValidateSmtp(
        SmtpDocument? smtp, List<ConfigurationError> errors, ref bool complete)
    {
        if (smtp is null)
        {
            return null;
        }

        bool usable = true;

        usable &= Required(smtp.Host, "alerting.smtp.host", errors, out string host);
        usable &= Required(smtp.From, "alerting.smtp.from", errors, out string from);

        int port = smtp.Port ?? DefaultSmtpPort;

        if (port is <= 0 or > 65_535)
        {
            errors.Add(new ConfigurationError("alerting.smtp.port", "must be between 1 and 65535"));
            usable = false;
        }

        string[] recipients = [.. (smtp.To ?? []).Select(entry => entry?.Trim() ?? "").Where(entry => entry.Length > 0)];

        if (recipients.Length == 0)
        {
            errors.Add(new ConfigurationError("alerting.smtp.to", "at least one recipient is required"));
            usable = false;
        }

        if (!string.IsNullOrWhiteSpace(smtp.Password))
        {
            errors.Add(new ConfigurationError(
                "alerting.smtp.password",
                "a password never goes in this file; name it with password_secret instead"));
            usable = false;
        }

        string? username = string.IsNullOrWhiteSpace(smtp.Username) ? null : smtp.Username.Trim();
        string? secret = string.IsNullOrWhiteSpace(smtp.PasswordSecret)
            ? null
            : smtp.PasswordSecret.Trim();

        // Either both or neither: half a credential authenticates nothing, and finding that
        // out at the relay means finding it out when the alert fails to leave.
        if (username is null && secret is not null)
        {
            errors.Add(new ConfigurationError(
                "alerting.smtp.username", "required when password_secret names a secret"));
            usable = false;
        }
        else if (username is not null && secret is null)
        {
            errors.Add(new ConfigurationError(
                "alerting.smtp.password_secret", "required when username names an account"));
            usable = false;
        }

        // A password on a connection that never starts TLS is a password on the wire. The
        // relay is inside the same rack as these two hosts, which is exactly the argument
        // that gets made right up until it is not true any more.
        if (username is not null && !smtp.StartTls)
        {
            errors.Add(new ConfigurationError(
                "alerting.smtp.start_tls",
                "required when the relay is given credentials; a password would otherwise "
                + "cross the network in the clear"));
            usable = false;
        }

        if (!usable)
        {
            complete = false;
            return null;
        }

        return new SmtpSettings(host, port, smtp.StartTls, from, recipients, username, secret);
    }

    /// Plain HTTP is refused rather than warned about: a webhook URL is a bearer token as
    /// often as not, and the body names every VM that would not come back.
    private static WebhookSettings? ValidateWebhook(
        WebhookDocument? webhook, List<ConfigurationError> errors, ref bool complete)
    {
        if (webhook is null)
        {
            return null;
        }

        if (!Uri.TryCreate(webhook.Url?.Trim(), UriKind.Absolute, out Uri? url)
            || url.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add(new ConfigurationError("alerting.webhook.url", "must be an https:// URL"));
            complete = false;
            return null;
        }

        return new WebhookSettings(url);
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

        // Zero would report a test failover as its own orphan on the first check of the run.
        if (replication.TestFailoverOrphanAfterHours is not (null or (> 0 and <= MaxOrphanHours)))
        {
            errors.Add(new ConfigurationError(
                "replication.test_failover_orphan_after_hours",
                $"must be between 1 and {MaxOrphanHours} when present"));
            complete = false;
        }

        return complete
            ? new ReplicationSettings(
                role,
                switchName,
                TimeSpan.FromSeconds(replication.ExpectedFrequencySec!.Value),
                replication.LagWarningMultiplier!.Value,
                replication.HealthWarningAfterSec is { } seconds
                    ? TimeSpan.FromSeconds(seconds)
                    : ReplicationSettings.DefaultHealthWarningAfter,
                testSwitch,
                replication.TestFailoverOrphanAfterHours is { } orphanHours
                    ? TimeSpan.FromHours(orphanHours)
                    : null)
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

        // Name inequality is not the guarantee — a second external switch reaches production
        // just as well. The switch's kind is checked against the host before anything starts;
        // this only catches the copy-paste at the point it is made.
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
                volume!, storage.FreeSpaceWarningGb!.Value, storage.CheckBitlockerAutounlock ?? false)
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
        //
        // Parsing is not enough on its own. IPAddress accepts the shorthand forms — `192.0.2`
        // is read as 192.0.0.2 — so a typo becomes a different, valid address rather than an
        // error: the firewall would open to a host nobody named and the listener would refuse
        // the real peer. Requiring the text to be what the address renders back to is what
        // turns that into the typo it is.
        if (complete
            && (!System.Net.IPAddress.TryParse(address, out System.Net.IPAddress? parsed)
                || !string.Equals(
                    parsed.ToString(), address, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add(new ConfigurationError(
                "peer.address", $"'{address}' is not an IP address written in full"));
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

    /// Two switches, off unless the file says otherwise. The only thing to validate is that
    /// they do not contradict each other: the release to install is the one looking found, so
    /// installing without looking is refused by name rather than quietly treated as off — the
    /// operator who wrote it believes this host updates itself.
    private static UpdateSettings? ValidateUpdates(
        UpdatesDocument? updates, List<ConfigurationError> errors)
    {
        if (updates is null)
        {
            return UpdateSettings.Disabled();
        }

        if (updates.Install && !updates.Check)
        {
            errors.Add(new ConfigurationError(
                "updates.install",
                "cannot be set without updates.check: the release to install is the one "
                + "checking finds"));

            return null;
        }

        return new UpdateSettings(updates.Check, updates.Install);
    }

    /// Off unless the file switches it on, and refused rather than corrected when a figure is
    /// out of range: a page bound to the wrong port is a page nobody finds, and silently
    /// moving it would be worse than refusing to start.
    private static DashboardSettings? ValidateDashboard(
        DashboardDocument? dashboard,
        ListenerSettings? listener,
        List<ConfigurationError> errors)
    {
        if (dashboard is null)
        {
            return DashboardSettings.Disabled();
        }

        bool complete = true;
        int port = dashboard.Port ?? DashboardSettings.DefaultPort;

        if (port is <= 0 or > 65_535)
        {
            errors.Add(new ConfigurationError("dashboard.port", "must be between 1 and 65535"));
            complete = false;
        }
        else if (listener is not null && port == listener.Port)
        {
            // Two sockets on one port means one of them fails to bind, and which one is
            // whichever started first.
            errors.Add(new ConfigurationError(
                "dashboard.port", "must differ from listener.port"));
            complete = false;
        }

        TimeSpan refresh = TimeSpan.FromSeconds(
            dashboard.RefreshSec ?? DashboardSettings.DefaultRefresh.TotalSeconds);

        if (refresh < DashboardSettings.MinimumRefresh
            || refresh > DashboardSettings.MaximumRefresh)
        {
            errors.Add(new ConfigurationError(
                "dashboard.refresh_sec",
                $"must be between {(int)DashboardSettings.MinimumRefresh.TotalSeconds} "
                + $"and {(int)DashboardSettings.MaximumRefresh.TotalSeconds} seconds"));
            complete = false;
        }

        return complete ? new DashboardSettings(dashboard.Enabled, port, refresh) : null;
    }

    /// No block at all is the documented off switch: the node degrades to the local-only
    /// view. A block that is present and enabled has to be complete, because a listener
    /// started without both thumbprints is a listener without mutual authentication.
    private static ListenerSettings? ValidateListener(
        ListenerDocument? listener, string? configurationPath, List<ConfigurationError> errors)
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
            ? WindowsPath.Join(
                WindowsPath.FolderOf(configurationPath), ListenerSettings.DefaultSnapshotFileName)
            : listener.SnapshotPath.Trim();

        // Access is granted on the folder rather than on the file, so there has to be one to
        // grant on. Refused rather than resolved against something: what a relative path means
        // depends on the working directory of whoever runs the command, and the service's is
        // not the operator's.
        //
        // Only what the operator wrote. A default with no folder means the caller supplied no
        // configuration path to place it beside, which is not something to refuse an operator
        // over — the CLI resolves that path in full before it ever gets here.
        if (listener.Enabled
            && listener.SnapshotPath is { Length: > 0 }
            && WindowsPath.FolderOf(snapshotPath).Length == 0)
        {
            errors.Add(new ConfigurationError(
                "listener.snapshot_path",
                "must name a folder as well as a file, because the service account is granted "
                + "access to the folder"));

            complete = false;
        }

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
        // Absent is a file nobody finished; `[]` is a host with no VM yet, written on purpose.
        if (vms is null)
        {
            errors.Add(new ConfigurationError("vms", "required: list the VMs, or [] for none"));
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

            // Absent means auto. Present and unreadable is refused by name, never by ordinal,
            // for the reason the priority is: reading `manaul` as `auto` sweeps up the one
            // machine the key was written to keep out.
            FailoverPolicy failover = FailoverPolicy.Auto;

            if (vm.Failover is not null
                && (!IsPolicyName(vm.Failover)
                    || !Enum.TryParse(vm.Failover.Trim(), ignoreCase: true, out failover)))
            {
                errors.Add(new ConfigurationError(
                    $"{path}.failover",
                    "when present, one of "
                        + string.Join(", ", Enum.GetNames<FailoverPolicy>()).ToLowerInvariant()));
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
                    vm.GuestOsSupportEnds is { } supportEnds ? AsUtcDate(supportEnds) : null,
                    failover));
            }
        }

        return settings;
    }

    private static bool IsPriorityName(string? value) =>
        Enum.GetNames<VmPriority>()
            .Contains(value?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);

    private static bool IsPolicyName(string? value) =>
        Enum.GetNames<FailoverPolicy>()
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
