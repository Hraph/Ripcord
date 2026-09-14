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
/// One of the facts this adapter asserts from documentation and has never observed.
internal static class WmiTestFailover
{
    private const string Namespace = @"root\virtualization\v2";

    /// Verified against the Microsoft `hyperv_v2` reference. The creation lives on the
    /// replication service; the destruction does **not** — a test VM is destroyed like any
    /// other VM, through the management service, and the reference says so explicitly on
    /// TestReplicaSystem's ResultingSystem parameter.
    private const string CreateTestSystem = "TestReplicaSystem";
    private const string DestroyTestSystem = "DestroySystem";

    /// A VM in test-replica mode. Discriminated on the mode, never on the `" - Test"` name
    /// suffix, whose localisation on a non-English host is unverifiable.
    private const string TestVmQuery =
        "SELECT ElementName, InstallDate FROM Msvm_ComputerSystem WHERE ReplicationMode = 3";

    private const string SwitchQuery =
        "SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch";

    /// CIM_EnabledLogicalElement requested states: 2 is Enabled ("start"), 3 is Disabled
    /// ("turn off"). Verified on Msvm_ComputerSystem.EnabledState.
    private const ushort Enabled = 2;
    private const ushort Disabled = 3;

    /// Classifies every virtual switch on the host, by the route Microsoft's own networking
    /// sample uses: the switch's ports through `Msvm_SystemDevice`, each port's allocation
    /// setting data through `Msvm_ElementSettingData`, and the class named by that setting's
    /// `HostResource`.
    ///
    /// An earlier version walked `Msvm_LANEndpoint` and `Msvm_ActiveConnection` instead. The
    /// classes were real but the route rested on two things the reference does not state: the
    /// direction of `Msvm_ActiveConnection` (documented as not mattering for a bidirectional
    /// connection, so querying one direction may find nothing) and `SystemName` on the peer
    /// endpoint being the switch GUID. This route needs neither.
    public static IReadOnlyList<HostSwitch> Switches(
        CimSession session, CimOperationOptions options)
    {
        List<HostSwitch> switches = [];

        foreach (CimInstance instance in session.QueryInstances(
            Namespace, "WQL", SwitchQuery, options))
        {
            using (instance)
            {
                if (CimValues.Text(instance, "ElementName") is not { } name)
                {
                    continue;
                }

                switches.Add(Classify(session, instance, name, options));
            }
        }

        return switches;
    }

    private static HostSwitch Classify(
        CimSession session, CimInstance virtualSwitch, string name, CimOperationOptions options)
    {
        bool reachesAPhysicalNic = false;
        bool reachesTheManagementOs = false;
        bool portsWereEnumerated = false;

        foreach (CimInstance port in session.EnumerateAssociatedInstances(
            Namespace, virtualSwitch, "Msvm_SystemDevice", "Msvm_EthernetSwitchPort",
            sourceRole: "GroupComponent", resultRole: "PartComponent", options))
        {
            using (port)
            {
                portsWereEnumerated = true;

                foreach (string bound in BoundResources(session, port, options))
                {
                    // The object path names the bound resource's class. Class names are not
                    // localized, unlike the switch and rule names this project refuses to
                    // match on elsewhere.
                    reachesAPhysicalNic |= bound.Contains(
                        "Msvm_ExternalEthernetPort", StringComparison.OrdinalIgnoreCase);

                    reachesTheManagementOs |= bound.Contains(
                        "Msvm_ComputerSystem", StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        return new HostSwitch(name, SwitchClassification.Of(
            reachesAPhysicalNic, reachesTheManagementOs, portsWereEnumerated));
    }

    /// `HostResource` holds the object path of whatever the port is bound to — a physical
    /// NIC, the management OS, or nothing at all for a port serving a VM. Only the first
    /// element is meaningful; the reference says only one host resource can be assigned.
    private static List<string> BoundResources(
        CimSession session, CimInstance port, CimOperationOptions options)
    {
        List<string> bound = [];

        foreach (CimInstance setting in session.EnumerateAssociatedInstances(
            Namespace, port, "Msvm_ElementSettingData",
            "Msvm_EthernetPortAllocationSettingData",
            sourceRole: "ManagedElement", resultRole: "SettingData", options))
        {
            using (setting)
            {
                if (setting.CimInstanceProperties["HostResource"]?.Value
                    is string[] { Length: > 0 } resources
                    && resources[0] is { } first)
                {
                    bound.Add(first);
                }
            }
        }

        return bound;
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

            // Null is documented as "the latest point in time", which is what a monthly test
            // wants. Passed explicitly rather than omitted so the intent is on the wire.
            CimMethodParameter.Create(
                "SnapshotSettingData", null, CimType.Reference, CimFlags.In),
        ];

        using CimInstance service = ReplicationService(session, options);

        using CimMethodResult result = session.InvokeMethod(
            Namespace, service, CreateTestSystem, parameters, options);

        WmiJob.Complete(session, result, options, CreateTestSystem);

        // The created system is returned by the method. Read from the out parameter rather
        // than derived from the replica's name, which is the whole reason the `" - Test"`
        // suffix never appears in this codebase.
        if (result.OutParameters["ResultingSystem"]?.Value is CimInstance created)
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

    /// Takes the **test** VM, not the replicated one, and turns it off first: DestroySystem
    /// is documented as requiring the machine to be powered off or saved, so destroying a
    /// running test VM returns Invalid State and leaves it behind — the orphan this milestone
    /// exists to prevent.
    public static void DestroyTestVm(
        CimSession session, CimInstance testVm, CimOperationOptions options)
    {
        TurnOff(session, testVm, options);

        using CimMethodParametersCollection parameters =
        [
            CimMethodParameter.Create(
                "AffectedSystem", testVm, CimType.Reference, CimFlags.In),
        ];

        using CimInstance service = VirtualSystemManagementService(session, options);

        using CimMethodResult result = session.InvokeMethod(
            Namespace, service, DestroyTestSystem, parameters, options);

        WmiJob.Complete(session, result, options, DestroyTestSystem);
    }

    /// Already off is not a failure: the VM may never have been started, or may have shut
    /// itself down. Only the destruction that follows has to succeed.
    private static void TurnOff(
        CimSession session, CimInstance testVm, CimOperationOptions options)
    {
        try
        {
            RequestState(session, testVm, Disabled, options);
        }
        catch (CimException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    public static void Start(
        CimSession session, CimInstance testVm, CimOperationOptions options) =>
        RequestState(session, testVm, Enabled, options);

    private static void RequestState(
        CimSession session, CimInstance testVm, ushort state, CimOperationOptions options)
    {
        using CimMethodParametersCollection parameters =
        [
            CimMethodParameter.Create("RequestedState", state, CimType.UInt16, CimFlags.In),
        ];

        using CimMethodResult result = session.InvokeMethod(
            Namespace, testVm, "RequestStateChange", parameters, options);

        WmiJob.Complete(session, result, options, "RequestStateChange");
    }

    /// `Msvm_HeartbeatComponent.OperationalStatus[0]`, reached through `Msvm_SystemDevice`.
    /// Codes are the documented ones: 2 OK, 3 Degraded (normal, on a negotiated protocol
    /// version), 7 the guest supports no compatible protocol, 12 not installed *or* not yet
    /// contacted, 13 lost communication, 15 the VM is paused.
    ///
    /// The component only exists while the VM runs, so no component means not running — not,
    /// as this adapter first assumed, a guest without integration services. That state is not
    /// reportable: code 12 covers both and Hyper-V does not separate them.
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
                    2 or 3 => Heartbeat.Ok,
                    12 or 13 => Heartbeat.NoContact,
                    7 or 15 => Heartbeat.CannotConfirm,
                    _ => Heartbeat.Unreadable,
                };
            }
        }

        return Heartbeat.NotRunning;
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
