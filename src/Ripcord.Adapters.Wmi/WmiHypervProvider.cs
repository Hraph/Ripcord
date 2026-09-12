using Microsoft.Management.Infrastructure.Options;
using System.Globalization;
using Microsoft.Management.Infrastructure;
using Ripcord.Domain.Replication;
using Ripcord.Ports.Replication;
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
                if (CimTranslation.IsVirtualMachine(CimValues.Instant(instance, "InstallDate")))
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
    /// PendingReplicationSize is not on the relationship at all; it comes from
    /// Msvm_ReplicationStatistics, through the service method below.
    private static VmReplicationState ReadVm(
        CimSession session, CimInstance vm, CimOperationOptions options)
    {
        using CimInstance? relationship = ReadRelationship(session, vm, options);

        return new VmReplicationState(
            CimTranslation.VmName(CimValues.Text(vm, "ElementName")),
            CimReplicationValues.Role(CimValues.Number(vm, "ReplicationMode")),
            CimReplicationValues.State(CimValues.Number(relationship, "ReplicationState")),
            CimReplicationValues.Health(CimValues.Number(relationship, "ReplicationHealth")),
            CimTranslation.Instant(CimValues.Instant(relationship, "LastReplicationTime")),
            ReadPendingBytes(session, vm, relationship, options));
    }

    /// Msvm_ReplicationStatistics cannot be queried — InstanceID, Caption, Description and
    /// ElementName are documented as always null — so the only way in is
    /// GetReplicationStatisticsEx. The Ex form takes the relationship, which is why the
    /// primary one is selected first: the older form silently reports the primary's numbers
    /// whatever is asked of it.
    ///
    /// Only the synchronous return is serviced. A return of 4096 means the provider started a
    /// job instead; that is not overlooked, it is declined — polling a CIM_ConcreteJob for one
    /// column is machinery no test off Windows could exercise. The volume then renders as
    /// unknown, the same as for a VM with no relationship.
    private static long? ReadPendingBytes(
        CimSession session,
        CimInstance vm,
        CimInstance? relationship,
        CimOperationOptions options)
    {
        if (relationship is null)
        {
            return null;
        }

        using CimMethodParametersCollection parameters =
        [
            CimMethodParameter.Create("ComputerSystem", vm, CimType.Reference, CimFlags.In),

            // The parameter carries the EmbeddedInstance qualifier. The Microsoft sample
            // serialises it with System.Management's GetText, which MI has no equivalent for,
            // so the instance goes across directly. This is the line most likely to be wrong
            // on the first real host — check it before anything else.
            CimMethodParameter.Create(
                "ReplicationRelationship", relationship, CimType.Instance, CimFlags.In),
        ];

        using CimMethodResult result = session.InvokeMethod(
            Namespace, ReplicationService(session, options), "GetReplicationStatisticsEx",
            parameters, options);

        if (Convert.ToUInt32(result.ReturnValue.Value, CultureInfo.InvariantCulture) != 0)
        {
            return null;
        }

        // Declared as a string array while the prose calls it a single embedded instance.
        // Read as an array and take the first: a scalar read would throw on real hardware.
        if (result.OutParameters["ReplicationStatistics"]?.Value is not CimInstance[]
            { Length: > 0 } statistics)
        {
            return null;
        }

        using CimInstance first = statistics[0];
        return CimValues.Size(first, "PendingReplicationSize");
    }

    private static CimInstance ReplicationService(
        CimSession session, CimOperationOptions options) =>
        session.QueryInstances(
            Namespace, "WQL", "SELECT * FROM Msvm_ReplicationService", options).First();

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
                && CimTranslation.IsPrimaryRelationship(CimValues.Text(relationship, "InstanceID")))
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
}
