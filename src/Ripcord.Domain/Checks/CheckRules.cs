namespace Ripcord.Domain.Checks;

/// How loudly a finding speaks, and whether it changes the exit code. Only Critical does
/// (decision D7): a warning that failed a scheduled task would be switched off within a week.
public enum Severity
{
    Critical,
    Warning,
    Info,
}

/// One rule, identified by a string that appears in `ripcord.yaml` acknowledgements. The id
/// is a contract with the operator's configuration file, so it never changes with a rename.
///
/// Acknowledgeable is the separate class milestone 2 requires: a rule whose violation means
/// the service does not come back at all cannot be silenced, because silencing it is
/// silencing the tool.
public sealed record CheckRule(string Id, Severity Severity, bool Acknowledgeable, string Title);

/// The catalogue. `ConfigurationValidator` reads it to refuse an acknowledgement of a rule
/// that does not exist — a misspelt rule id is an operator who believes something is
/// suppressed when it is not.
public static class CheckRules
{
    public const string ReplicaSwitchMismatch = "replica-switch-mismatch";
    public const string ReplicaAdapterDisconnected = "replica-adapter-disconnected";
    public const string ReplicaMacDrift = "replica-mac-drift";
    public const string VlanMismatch = "vlan-mismatch";
    public const string StartupRamExceedsTarget = "startup-ram-exceeds-target";
    public const string P1StartupRamSumExceedsTarget = "p1-startup-ram-sum-exceeds-target";
    public const string PassthroughDiskOnReplicatedVm = "passthrough-disk-on-replicated-vm";
    public const string VhdxOutsideRelationship = "vhdx-outside-relationship";
    public const string ReplicationDirectionInverted = "replication-direction-inverted";
    public const string TargetBitlockerWithoutAutounlock = "target-bitlocker-without-autounlock";

    public const string ReplicationHealthDegraded = "replication-health-degraded";
    public const string LagBeyondThreshold = "lag-beyond-threshold";
    public const string ResynchronizationInProgress = "resynchronization-in-progress";
    public const string CertificateNearExpiry = "certificate-near-expiry";
    public const string FreeSpaceBelowThreshold = "free-space-below-threshold";
    public const string DynamicMaximumExceedsTarget = "dynamic-maximum-exceeds-target";
    public const string StartupRamDrift = "startup-ram-drift";

    public const string VmWithoutRelationship = "vm-without-relationship";
    public const string PassthroughDiskIsSolePointInTimeCopy =
        "passthrough-disk-is-sole-point-in-time-copy";
    public const string NoBackupsWhileFailedOver = "no-backups-while-failed-over";
    public const string GuestOsOutOfSupport = "guest-os-out-of-support";
    public const string DomainControllerPresent = "domain-controller-present";

    /// Declared in the order the report prints them: criticals first, and within a severity
    /// the network rules before the capacity rules, because that is the order of the day.
    public static readonly IReadOnlyList<CheckRule> All =
    [
        new(ReplicaSwitchMismatch, Severity.Critical, true,
            "Replica attached to an unexpected virtual switch"),
        new(ReplicaAdapterDisconnected, Severity.Critical, true,
            "Replica network adapter bound to no switch"),
        new(ReplicaMacDrift, Severity.Critical, true,
            "Replica MAC address dynamic, or different from the primary's"),
        new(VlanMismatch, Severity.Critical, true,
            "VLAN identifier differs between the two sides"),
        new(StartupRamExceedsTarget, Severity.Critical, false,
            "Startup RAM exceeds the target's usable memory"),
        new(P1StartupRamSumExceedsTarget, Severity.Critical, false,
            "The P1 VMs together exceed the target's usable memory"),
        new(PassthroughDiskOnReplicatedVm, Severity.Critical, true,
            "Pass-through disk on a replicated VM"),
        new(VhdxOutsideRelationship, Severity.Critical, false,
            "A VHDX attached here is absent from the replication relationship"),
        new(ReplicationDirectionInverted, Severity.Critical, true,
            "Replication runs in the wrong direction"),
        new(TargetBitlockerWithoutAutounlock, Severity.Critical, true,
            "The target's data volume is BitLocker-protected without auto-unlock"),

        new(ReplicationHealthDegraded, Severity.Warning, true,
            "Replication health has not been Normal for some time"),
        new(LagBeyondThreshold, Severity.Warning, true,
            "Replication lag beyond the configured multiple of the frequency"),
        new(ResynchronizationInProgress, Severity.Warning, true,
            "Resynchronization in progress"),
        new(CertificateNearExpiry, Severity.Warning, true,
            "Replication certificate close to expiry"),
        new(FreeSpaceBelowThreshold, Severity.Warning, true,
            "Free space on the data volume below the threshold"),
        new(DynamicMaximumExceedsTarget, Severity.Warning, true,
            "Dynamic maximum exceeds the target's usable memory"),
        new(StartupRamDrift, Severity.Warning, true,
            "Startup RAM differs from the configured expectation"),

        new(VmWithoutRelationship, Severity.Info, true,
            "VM present here with no replication relationship"),
        new(PassthroughDiskIsSolePointInTimeCopy, Severity.Info, true,
            "A pass-through disk holds the only point-in-time copy"),
        new(NoBackupsWhileFailedOver, Severity.Info, true,
            "No backups run while failed over"),
        new(GuestOsOutOfSupport, Severity.Info, true,
            "Guest operating system out of support"),
        new(DomainControllerPresent, Severity.Info, true,
            "A domain controller is in the failover scope"),
    ];

    public static CheckRule? ById(string? id) =>
        All.FirstOrDefault(rule => rule.Id == id);
}
