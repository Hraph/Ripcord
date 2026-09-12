using Microsoft.Management.Infrastructure.Options;
using Microsoft.Management.Infrastructure;
using Ripcord.Domain.Replication;
using Ripcord.Ports;

namespace Ripcord.Adapters.Wmi;

/// `root\virtualization\v2` to domain models, and nothing else. Every lookup, fallback and
/// conversion is in `CimTranslation` or `CimReplicationValues`. This class cannot be run off
/// Windows, so anything it decided would be a decision nobody could test before the day it
/// mattered.
public sealed class WmiHypervProvider(string localHostName, TimeSpan timeout) : IHypervProvider
{
    private const string Namespace = @"root\virtualization\v2";

    /// Msvm_ComputerSystem holds the host as well as its VMs. The filtering rule is
    /// `CimTranslation.IsVirtualMachine`; nothing is discriminated here on a localized string.
    private const string ComputerSystemQuery =
        "SELECT ElementName, InstallDate, ReplicationMode FROM Msvm_ComputerSystem";

    public Task<HostState> GetLocalStateAsync(CancellationToken cancellationToken) =>
        Task.Run(() => this.ReadLocalState(cancellationToken), cancellationToken);

    /// Milestone 1 is local-only: there is no channel to the peer, and saying so is the
    /// behaviour, not a stub. Milestone 1b puts a transport behind this and changes nothing
    /// above it. The name is left empty because a host that never answered cannot supply one;
    /// the Application names an unreachable peer from the configuration.
    public Task<HostState> GetPeerStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(HostState.Unreachable(string.Empty, HostReachability.NotConfigured()));

    private HostState ReadLocalState(CancellationToken cancellationToken)
    {
        CimOperationOptions options =
            new() { Timeout = timeout, CancellationToken = cancellationToken };

        using CimSession session = CimSession.Create(computerName: null);

        List<VmReplicationState> vms = [];

        foreach (CimInstance instance in session.QueryInstances(
            Namespace, "WQL", ComputerSystemQuery, options))
        {
            using (instance)
            {
                if (CimTranslation.IsVirtualMachine(Instant(instance, "InstallDate")))
                {
                    vms.Add(ReadVm(session, instance, options));
                }
            }
        }

        return new HostState(localHostName, vms, HostReachability.Reachable());
    }

    /// Replication state, health and timings come from Msvm_ReplicationRelationship. The
    /// equivalents on Msvm_ComputerSystem are deprecated since Windows 8.1 and still return
    /// values — but only the *primary* relationship's, so with extended replication they
    /// silently report the wrong one. ReplicationMode is the exception: it exists only on the
    /// computer system. Do not fold these back onto one class.
    ///
    /// PendingReplicationSize is not on the relationship at all; it is on
    /// Msvm_ReplicationStatistics, reachable only through
    /// Msvm_ReplicationService.GetReplicationStatisticsEx, which may complete asynchronously.
    /// Milestone 1 leaves the pending volume unknown rather than guessing a method signature
    /// that cannot be exercised off Windows — see the known gap in milestone-1.md.
    private static VmReplicationState ReadVm(
        CimSession session, CimInstance vm, CimOperationOptions options)
    {
        using CimInstance? relationship = ReadRelationship(session, vm, options);

        return new VmReplicationState(
            CimTranslation.VmName(Text(vm, "ElementName")),
            CimReplicationValues.Role(Number(vm, "ReplicationMode")),
            CimReplicationValues.State(Number(relationship, "ReplicationState")),
            CimReplicationValues.Health(Number(relationship, "ReplicationHealth")),
            CimTranslation.Instant(Instant(relationship, "LastReplicationTime")),
            PendingBytes: null);
    }

    /// The association is named rather than left null: a VM's Msvm_ComputerSystem sits at the
    /// end of many associations, and an unnamed traversal returns a heterogeneous set to
    /// type-filter afterwards. With extended replication there are several relationships, so
    /// the primary one is picked deliberately.
    ///
    /// A VM without replication simply has none, which is a null here rather than an error.
    private static CimInstance? ReadRelationship(
        CimSession session, CimInstance vm, CimOperationOptions options)
    {
        CimInstance? primary = null;

        foreach (CimInstance relationship in session.EnumerateAssociatedInstances(
            Namespace,
            vm,
            associationClassName: "Msvm_SystemReplicationRelationship",
            resultClassName: "Msvm_ReplicationRelationship",
            sourceRole: "Antecedent",
            resultRole: "Dependent",
            options))
        {
            // Every instance is disposed, not only the one kept: these are native handles, and
            // extended replication enumerates one we do not want.
            if (primary is null
                && CimTranslation.IsPrimaryRelationship(Text(relationship, "InstanceID")))
            {
                primary = relationship;
            }
            else
            {
                relationship.Dispose();
            }
        }

        return primary;
    }

    /// Properties are read by name and tolerated absent: the exact set differs between
    /// Windows Server builds, and a missing property must degrade to "unknown" rather than
    /// throw halfway through an inventory.
    private static object? Value(CimInstance? instance, string propertyName) =>
        instance?.CimInstanceProperties[propertyName]?.Value;

    private static string? Text(CimInstance? instance, string propertyName) =>
        Value(instance, propertyName) as string;

    private static ushort? Number(CimInstance? instance, string propertyName) =>
        Value(instance, propertyName) is ushort number ? number : null;

    private static DateTime? Instant(CimInstance? instance, string propertyName) =>
        Value(instance, propertyName) is DateTime value ? value : null;

}
