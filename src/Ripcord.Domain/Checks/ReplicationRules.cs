using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;

namespace Ripcord.Domain.Checks;

/// Replication itself: is it running, is it keeping up, and is it running the right way
/// round. Health, lag and resynchronization are read from the **local** host's own VMs —
/// those figures are always readable, whereas the peer's arrive in a snapshot that may be
/// stale or absent, and `check` run on either host should answer for the side it is on.
internal static class ReplicationRules
{
    /// The states that mean a resynchronization, which can run for weeks with nobody noticing.
    private static readonly ReplicationState[] Resynchronizing =
    [
        ReplicationState.WaitingToStartResynchronization,
        ReplicationState.Resynchronizing,
        ReplicationState.ResynchronizationSuspended,
    ];

    public static IEnumerable<Finding> Evaluate(CheckSubject subject)
    {
        foreach (Finding finding in LocalVms(subject))
        {
            yield return finding;
        }

        foreach (Finding finding in Unreplicated(subject))
        {
            yield return finding;
        }

        foreach (Finding finding in Direction(subject))
        {
            yield return finding;
        }

        foreach (Finding finding in Certificates(subject))
        {
            yield return finding;
        }
    }

    private static IEnumerable<Finding> LocalVms(CheckSubject subject)
    {
        foreach (VmReplicationState vm in subject.Local.Vms
            .Where(vm => vm.Role != ReplicationRole.None))
        {
            foreach (Finding finding in Health(subject, vm))
            {
                yield return finding;
            }

            foreach (Finding finding in Lag(subject, vm))
            {
                yield return finding;
            }

            if (Resynchronizing.Contains(vm.State))
            {
                yield return Found.Violated(
                    CheckRules.ResynchronizationInProgress,
                    vm.Name,
                    $"{vm.Name} is resynchronizing on {subject.Local.HostName}",
                    "until it completes the replica is not a usable copy; a "
                    + "resynchronization can run for weeks with nobody noticing",
                    $"Get-VMReplication -VMName {vm.Name} and let it finish, or "
                    + "Resume-VMReplication if it is suspended");
            }
        }
    }

    /// Hyper-V's health flickers to Warning for a single missed cycle, so the threshold is
    /// the configured one rather than zero. Nothing here records *when* health went bad —
    /// that needs the history of milestone 6 — so the time since the last successful
    /// replication stands in for it, which is the only duration this milestone can observe.
    private static IEnumerable<Finding> Health(CheckSubject subject, VmReplicationState vm)
    {
        if (vm.Health == ReplicationHealth.Unknown)
        {
            yield return Found.Unevaluable(
                CheckRules.ReplicationHealthDegraded,
                vm.Name,
                $"{vm.Name} has a relationship on {subject.Local.HostName} but reported no "
                + "replication health");

            yield break;
        }

        if (vm.Health == ReplicationHealth.Normal)
        {
            yield break;
        }

        TimeSpan? since = vm.LagAt(subject.Now);
        TimeSpan threshold = subject.Configuration.Replication.HealthWarningAfter;

        if (since is { } elapsed && elapsed < threshold)
        {
            yield break;
        }

        yield return Found.Violated(
            CheckRules.ReplicationHealthDegraded,
            vm.Name,
            $"{vm.Name} reports health {vm.Health} on {subject.Local.HostName}, and "
            + (since is { } waited
                ? $"has not replicated for {Describe(waited)}"
                : "has never replicated"),
            "the replica is behind or broken; a failover now loses whatever has not "
            + "crossed",
            $"Get-VMReplication -VMName {vm.Name} for the reason, then "
            + "Resume-VMReplication once it is fixed");
    }

    private static IEnumerable<Finding> Lag(CheckSubject subject, VmReplicationState vm)
    {
        ReplicationSettings replication = subject.Configuration.Replication;

        if (vm.LagAt(subject.Now) is not { } lag)
        {
            yield return Found.Violated(
                CheckRules.LagBeyondThreshold,
                vm.Name,
                $"{vm.Name} has a relationship on {subject.Local.HostName} and has never "
                + "replicated",
                "there is no usable copy of this VM on the other side at all",
                $"Start-VMInitialReplication -VMName {vm.Name}");

            yield break;
        }

        TimeSpan threshold = replication.ExpectedFrequency * replication.LagWarningMultiplier;

        if (lag > threshold)
        {
            yield return Found.Violated(
                CheckRules.LagBeyondThreshold,
                vm.Name,
                $"{vm.Name} last replicated {Describe(lag)} ago, beyond "
                + $"{replication.LagWarningMultiplier} × "
                + $"{(int)replication.ExpectedFrequency.TotalSeconds}s",
                "a failover now loses everything written since then",
                $"Get-VMReplication -VMName {vm.Name} for the reason");
        }
    }

    /// Observed on the source, not filtered through the configured list: a VM nobody declared
    /// and nobody replicates is exactly what the operator needs to notice.
    private static IEnumerable<Finding> Unreplicated(CheckSubject subject)
    {
        if (!subject.Source.IsReachable)
        {
            yield return Found.Unevaluable(
                CheckRules.VmWithoutRelationship,
                null,
                $"{subject.Source.HostName} could not be read, so its VMs could not be "
                + "listed");

            yield break;
        }

        foreach (VmReplicationState vm in subject.Source.Vms
            .Where(vm => vm.Role == ReplicationRole.None))
        {
            bool declared = subject.Configuration.Vms
                .Any(settings => string.Equals(
                    settings.Name, vm.Name, StringComparison.OrdinalIgnoreCase));

            yield return Found.Violated(
                CheckRules.VmWithoutRelationship,
                vm.Name,
                $"{vm.Name} runs on {subject.Source.HostName} with no replication "
                + "relationship"
                + (declared ? " and is declared in ripcord.yaml" : ""),
                "this VM does not exist on the target; a failover leaves it behind",
                $"Enable-VMReplication -VMName {vm.Name}, or remove it from ripcord.yaml");
        }
    }

    /// True by definition while running on the disaster recovery side, which is why the
    /// operating mode suppresses it (decision D20): otherwise `check` is red for as long as
    /// the incident lasts, and `failback` would be blocked by the state it exists to repair.
    ///
    /// What remains in normal mode is a *partial* inversion — some VMs primary on one host and
    /// some on the other, which is maintenance that was never switched back.
    private static IEnumerable<Finding> Direction(CheckSubject subject)
    {
        if (subject.Mode == OperatingMode.FailedOver)
        {
            yield break;
        }

        if (!subject.Target.IsReachable)
        {
            yield return Found.Unevaluable(
                CheckRules.ReplicationDirectionInverted,
                null,
                $"{subject.Target.HostName} could not be read, so the direction of "
                + "replication could not be confirmed");

            yield break;
        }

        foreach (VmSettings settings in subject.Configuration.Vms)
        {
            if (CheckSubject.Find(subject.Target, settings.Name)?.Role
                != ReplicationRole.Primary)
            {
                continue;
            }

            yield return Found.Violated(
                CheckRules.ReplicationDirectionInverted,
                settings.Name,
                $"{settings.Name} is the primary copy on {subject.Target.HostName}, which "
                + "normally holds the replicas",
                "this VM is already running on the disaster recovery side while the rest of "
                + "the pair is not; a failover of the others would split the estate across "
                + "both hosts",
                $"Start-VMFailback -VMName {settings.Name} on {subject.Target.HostName} "
                + "once replication is reversed");
        }
    }

    /// Both hosts' certificates, each found by thumbprint in its own store (decision D6) and
    /// carried in that host's published snapshot. A thumbprint naming nothing is a finding,
    /// not a crash: the operator configured a certificate that is not there.
    private static IEnumerable<Finding> Certificates(CheckSubject subject)
    {
        if (!subject.Configuration.Listener.Enabled)
        {
            yield break;
        }

        foreach (HostState host in new[] { subject.Local, Other(subject) })
        {
            foreach (Finding finding in Certificate(subject, host))
            {
                yield return finding;
            }
        }
    }

    private static IEnumerable<Finding> Certificate(CheckSubject subject, HostState host)
    {
        if (!host.IsReachable)
        {
            yield return Found.Unevaluable(
                CheckRules.CertificateNearExpiry,
                null,
                $"{host.HostName} could not be read, so its certificate could not be "
                + "checked");

            yield break;
        }

        if (host.Facts?.Certificate is not { } certificate)
        {
            yield return Found.Unevaluable(
                CheckRules.CertificateNearExpiry,
                null,
                $"no certificate with the configured thumbprint was found on "
                + host.HostName);

            yield break;
        }

        if (!certificate.ExpiresWithin(CertificateWindow, subject.Now))
        {
            yield break;
        }

        yield return Found.Violated(
            CheckRules.CertificateNearExpiry,
            null,
            certificate.HasExpiredAt(subject.Now)
                ? $"the certificate of {host.HostName} ({certificate.CommonName}) expired on "
                    + $"{certificate.NotAfter:yyyy-MM-dd}"
                : $"the certificate of {host.HostName} ({certificate.CommonName}) expires on "
                    + $"{certificate.NotAfter:yyyy-MM-dd}",
            "Hyper-V Replica authenticates with this certificate; once it expires "
            + "replication stops, and so does the pair view",
            $"renew the certificate on {host.HostName} and update the thumbprints in "
            + "ripcord.yaml on both hosts");
    }

    /// Sixty days: long enough to raise a change request, short enough to still be true.
    private static readonly TimeSpan CertificateWindow = TimeSpan.FromDays(60);

    private static HostState Other(CheckSubject subject) =>
        subject.TargetIsLocal ? subject.Source : subject.Target;

    private static string Describe(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h{span.Minutes:00}m"
            : $"{(int)span.TotalMinutes}m{span.Seconds:00}s";
}
