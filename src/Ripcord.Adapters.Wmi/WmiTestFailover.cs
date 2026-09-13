using System.Diagnostics;
using System.Globalization;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;
using Ripcord.Domain.TestFailover;

namespace Ripcord.Adapters.Wmi;

/// The milestone 3 operations, mapped and nothing more. Every decision — whether a test VM is
/// isolated, whether it counts as an orphan, whether the sequence may proceed — lives in the
/// Domain, because a decision here is a decision nobody can test until someone is on Windows.
///
/// **Every method name and parameter name below is unverified.** They are written from the
/// Microsoft `hyperv_v2` reference and have never met a host; milestone 1 established that
/// three such assumptions out of three were wrong. They are gathered here rather than spread
/// through the file so a single session on the real hardware can correct them in one place.
/// See the V-items in `docs/TRACKING.md`.
internal static class WmiTestFailover
{
    private const string Namespace = @"root\virtualization\v2";

    /// Unverified. `Get-VMFailover`'s test operations are documented as living on the
    /// replication service, but the exact spellings are not closable from the reference.
    private const string CreateTestSystem = "CreateTestVirtualSystem";
    private const string DestroyTestSystem = "DestroyTestVirtualSystem";

    /// A VM in test-replica mode. Discriminated on the mode, never on the `" - Test"` name
    /// suffix, whose localisation on a non-English host is unverifiable.
    private const string TestVmQuery =
        "SELECT ElementName, InstallDate FROM Msvm_ComputerSystem WHERE ReplicationMode = 3";

    private const string SwitchQuery =
        "SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch";

    /// Requested state 2 is Enabled — the CIM spelling of "start".
    private const ushort Enabled = 2;

    public static IReadOnlyList<HostSwitch> Switches(
        CimSession session, CimOperationOptions options)
    {
        HashSet<string> external = SwitchesReachingAPhysicalNic(session, options);
        HashSet<string> internalSwitches = SwitchesReachingTheManagementOs(session, options);

        // The judgement about whether an empty external set is a fact or a broken traversal
        // lives in the Domain, where it is testable: SwitchClassification.Of. All the adapter
        // establishes is what the associations reported.
        bool externalTraversalProven = external.Count > 0;

        List<HostSwitch> switches = [];

        foreach (CimInstance instance in session.QueryInstances(
            Namespace, "WQL", SwitchQuery, options))
        {
            using (instance)
            {
                string? id = CimValues.Text(instance, "Name");
                string? name = CimValues.Text(instance, "ElementName");

                if (name is null || id is null)
                {
                    continue;
                }

                switches.Add(new HostSwitch(name, SwitchClassification.Of(
                    external.Contains(id),
                    internalSwitches.Contains(id),
                    externalTraversalProven)));
            }
        }

        return switches;
    }

    public static IReadOnlyList<TestVm> TestVms(
        CimSession session,
        IReadOnlyDictionary<string, string> switches,
        CimOperationOptions options)
    {
        List<TestVm> testVms = [];

        foreach (CimInstance instance in session.QueryInstances(
            Namespace, "WQL", TestVmQuery, options))
        {
            using (instance)
            {
                testVms.Add(new TestVm(
                    CimTranslation.VmName(CimValues.Text(instance, "ElementName")),

                    // A test VM's InstallDate is when the test failover created it, which is
                    // exactly the age the orphan rule needs.
                    CimTranslation.Instant(CimValues.Instant(instance, "InstallDate")),
                    WmiVmInventory.ReadAdapters(session, instance, switches, options)));
            }
        }

        return testVms;
    }

    /// Points the replica's test-failover adapters at a switch, or at nothing. Without this a
    /// test VM inherits the replica's production switch, and the isolation rule then refuses
    /// it — correctly, and every single time.
    public static void AttachTestNetwork(
        CimSession session,
        CimInstance vm,
        string? switchName,
        IReadOnlyDictionary<string, string> switches,
        CimOperationOptions options)
    {
        string testSwitchId = switchName is null
            ? string.Empty
            : switches.FirstOrDefault(entry =>
                string.Equals(entry.Value, switchName, StringComparison.OrdinalIgnoreCase)).Key
                ?? throw new InvalidOperationException(
                    $"no virtual switch on this host is called '{switchName}'");

        foreach (CimInstance allocation in WmiVmInventory.AdapterAllocations(
            session, vm, options))
        {
            using (allocation)
            {
                // TestReplicaSwitchName is read/write on Msvm_EthernetPortAllocationSettingData,
                // which is what makes a test VM's network configurable independently of the
                // replica's. Empty means "attached to nothing".
                //
                // The milestone names TestReplicaPoolID alongside it and this does not set it
                // (V30). If the pool identifier turns out to be required for the reassignment
                // to take, the test VM stays on the replica's switch and the isolation rule
                // refuses it — the safe direction, but a confusing refusal rather than a boot.
                allocation.CimInstanceProperties["TestReplicaSwitchName"].Value = testSwitchId;

                ModifyResourceSettings(session, allocation, options);
            }
        }
    }

    public static string CreateTestVm(
        CimSession session, CimInstance vm, CimOperationOptions options)
    {
        using CimMethodParametersCollection parameters =
        [
            CimMethodParameter.Create("ComputerSystem", vm, CimType.Reference, CimFlags.In),
        ];

        using CimInstance service = ReplicationService(session, options);

        using CimMethodResult result = session.InvokeMethod(
            Namespace, service, CreateTestSystem, parameters, options);

        WmiJob.Complete(session, result, options, CreateTestSystem);

        // The created system is returned by the method. Read from the out parameter rather
        // than derived from the replica's name, which is the whole reason the `" - Test"`
        // suffix never appears in this codebase.
        if (result.OutParameters["TestVirtualSystem"]?.Value is CimInstance created)
        {
            using (created)
            {
                if (CimTranslation.VmName(CimValues.Text(created, "ElementName")) is
                    { Length: > 0 } name)
                {
                    return name;
                }
            }
        }

        throw new InvalidOperationException(
            "the test VM was created but Hyper-V did not name it, so it cannot be started "
                + "or destroyed by this run");
    }

    public static void DestroyTestVm(
        CimSession session, CimInstance vm, CimOperationOptions options)
    {
        using CimMethodParametersCollection parameters =
        [
            CimMethodParameter.Create("ComputerSystem", vm, CimType.Reference, CimFlags.In),
        ];

        using CimInstance service = ReplicationService(session, options);

        using CimMethodResult result = session.InvokeMethod(
            Namespace, service, DestroyTestSystem, parameters, options);

        WmiJob.Complete(session, result, options, DestroyTestSystem);
    }

    public static void Start(
        CimSession session, CimInstance testVm, CimOperationOptions options)
    {
        using CimMethodParametersCollection parameters =
        [
            CimMethodParameter.Create("RequestedState", Enabled, CimType.UInt16, CimFlags.In),
        ];

        using CimMethodResult result = session.InvokeMethod(
            Namespace, testVm, "RequestStateChange", parameters, options);

        WmiJob.Complete(session, result, options, "RequestStateChange");
    }

    /// Msvm_Heartbeat's OperationalStatus: 2 is OK, 12 is "no contact", 13 is "lost
    /// communication". The instance exists only while the VM runs and only when the guest has
    /// the integration services, so its absence is NotInstalled rather than a failure.
    public static Heartbeat ReadHeartbeat(
        CimSession session, CimInstance testVm, CimOperationOptions options)
    {
        foreach (CimInstance component in session.EnumerateAssociatedInstances(
            Namespace, testVm, "Msvm_SystemDevice", "Msvm_HeartbeatComponent",
            sourceRole: "GroupComponent", resultRole: "PartComponent", options))
        {
            using (component)
            {
                if (component.CimInstanceProperties["OperationalStatus"]?.Value
                    is not ushort[] { Length: > 0 } status)
                {
                    return Heartbeat.Unreadable;
                }

                return status[0] switch
                {
                    2 => Heartbeat.Ok,
                    12 or 13 => Heartbeat.NoContact,
                    _ => Heartbeat.Unreadable,
                };
            }
        }

        // The same reasoning as the switch classification, one notch less dangerous: absence
        // of a heartbeat component is read as "the guest has none". It is never a pass, so a
        // wrong association here costs an unconfirmed boot rather than an unsafe one.
        return Heartbeat.NotInstalled;
    }

    /// A switch bridged to a physical NIC. This is the property that makes a test VM unsafe;
    /// the switch's name says nothing about it.
    private static HashSet<string> SwitchesReachingAPhysicalNic(
        CimSession session, CimOperationOptions options) =>
        SwitchesReaching(session, "Msvm_ExternalEthernetPort", options);

    private static HashSet<string> SwitchesReachingTheManagementOs(
        CimSession session, CimOperationOptions options) =>
        SwitchesReaching(session, "Msvm_InternalEthernetPort", options);

    /// Port to endpoint to active connection to the switch port, whose SystemName is the
    /// switch. Unverified, and deliberately fail-closed: a traversal that returns nothing
    /// leaves every switch unclassified, and an unclassified switch is refused rather than
    /// trusted.
    private static HashSet<string> SwitchesReaching(
        CimSession session, string portClass, CimOperationOptions options)
    {
        HashSet<string> reached = new(StringComparer.OrdinalIgnoreCase);

        foreach (CimInstance port in session.QueryInstances(
            Namespace, "WQL", $"SELECT * FROM {portClass}", options))
        {
            using (port)
            {
                foreach (CimInstance endpoint in session.EnumerateAssociatedInstances(
                    Namespace, port, "Msvm_EthernetDeviceSAPImplementation",
                    "Msvm_LANEndpoint", sourceRole: "Antecedent", resultRole: "Dependent",
                    options))
                {
                    using (endpoint)
                    {
                        foreach (CimInstance peer in session.EnumerateAssociatedInstances(
                            Namespace, endpoint, "Msvm_ActiveConnection", "Msvm_LANEndpoint",
                            sourceRole: "Antecedent", resultRole: "Dependent", options))
                        {
                            using (peer)
                            {
                                if (CimValues.Text(peer, "SystemName") is { } switchId)
                                {
                                    reached.Add(switchId);
                                }
                            }
                        }
                    }
                }
            }
        }

        return reached;
    }

    private static void ModifyResourceSettings(
        CimSession session, CimInstance allocation, CimOperationOptions options)
    {
        using CimMethodParametersCollection parameters =
        [
            CimMethodParameter.Create(
                "ResourceSettings", new[] { allocation }, CimType.InstanceArray, CimFlags.In),
        ];

        using CimInstance service = VirtualSystemManagementService(session, options);

        using CimMethodResult result = session.InvokeMethod(
            Namespace, service, "ModifyResourceSettings", parameters, options);

        WmiJob.Complete(session, result, options, "ModifyResourceSettings");
    }

    private static CimInstance ReplicationService(
        CimSession session, CimOperationOptions options) =>
        Singleton(session, "Msvm_ReplicationService", options);

    private static CimInstance VirtualSystemManagementService(
        CimSession session, CimOperationOptions options) =>
        Singleton(session, "Msvm_VirtualSystemManagementService", options);

    private static CimInstance Singleton(
        CimSession session, string className, CimOperationOptions options) =>
        session.QueryInstances(Namespace, "WQL", $"SELECT * FROM {className}", options).First();
}

/// A mutating CIM call that returned 4096 started a job rather than finishing. For a read
/// that can be declined; for an operation that creates or destroys a VM it cannot, because
/// the caller would go on to inspect a test VM that does not exist yet — or report a cleanup
/// that has not happened.
internal static class WmiJob
{
    private const uint Completed = 0;
    private const uint Started = 4096;

    /// CIM_ConcreteJob.JobState: 7 completed, 8 terminated, 9 killed, 10 exception.
    private const ushort CompletedState = 7;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// A create or destroy that has not finished in ten minutes is a failure worth reporting,
    /// not worth waiting on.
    private static readonly TimeSpan JobDeadline = TimeSpan.FromMinutes(10);

    public static void Complete(
        CimSession session, CimMethodResult result, CimOperationOptions options, string method)
    {
        uint returned = Convert.ToUInt32(
            result.ReturnValue.Value, CultureInfo.InvariantCulture);

        if (returned == Completed)
        {
            return;
        }

        if (returned != Started)
        {
            throw new InvalidOperationException($"{method} failed with code {returned}");
        }

        if (result.OutParameters["Job"]?.Value is not CimInstance job)
        {
            throw new InvalidOperationException(
                $"{method} started a job and did not say which, so its outcome is unknown");
        }

        Await(session, job, options, method, JobDeadline);
    }

    /// The per-operation timeout on `options` bounds each `GetInstance`, not the loop, so the
    /// loop carries a deadline of its own. Without it a job parked in Running polls every two
    /// seconds forever: `ripcord test-failover` never returns and never prints its report,
    /// with an operator watching a blank console during the monthly test.
    private static void Await(
        CimSession session,
        CimInstance job,
        CimOperationOptions options,
        string method,
        TimeSpan deadline)
    {
        using (job)
        {
            Stopwatch elapsed = Stopwatch.StartNew();

            while (true)
            {
                using CimInstance current = session.GetInstance(
                    job.CimSystemProperties.Namespace, job, options);

                // Not defaulted to an early state: 0 is not a valid CIM_ConcreteJob state at
                // all — they begin at 2 — so treating an unread property as "still starting"
                // would poll until the deadline every time. If JobState is the wrong property
                // name, which is exactly what blind writing produces, this says so instead of
                // hanging.
                if (current.CimInstanceProperties["JobState"]?.Value is not { } raw)
                {
                    throw new InvalidOperationException(
                        $"{method} started a job whose state cannot be read, so its outcome "
                            + "is unknown");
                }

                ushort state = Convert.ToUInt16(raw, CultureInfo.InvariantCulture);

                if (state == CompletedState)
                {
                    return;
                }

                if (state > CompletedState)
                {
                    throw new InvalidOperationException(
                        $"{method} ended in job state {state}: "
                            + (CimValues.Text(current, "ErrorDescription") ?? "no detail"));
                }

                if (elapsed.Elapsed >= deadline)
                {
                    throw new InvalidOperationException(
                        $"{method} was still in job state {state} after "
                            + $"{(int)deadline.TotalMinutes} minutes");
                }

                Thread.Sleep(PollInterval);
            }
        }
    }
}
