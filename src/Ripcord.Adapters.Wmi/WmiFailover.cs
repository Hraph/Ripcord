using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Ripcord.Domain.Replication;

namespace Ripcord.Adapters.Wmi;

/// The mutating failover operations, as CIM calls.
///
/// The class and method names here were **read from the Microsoft reference**, not recalled:
/// `Msvm_ReplicationService.InitiateFailover`, `ReverseReplicationRelationship` and
/// `RevertFailover` all appear in the published member list, and their MOF signatures are
/// reproduced faithfully below. `Msvm_ShutdownComponent.InitiateShutdown` is likewise
/// documented; its `Force` and `Reason` parameters are the one detail taken on trust rather
/// than read, and V37 says so.
///
/// Documented is still not verified: none of it has run on this hardware, which is the
/// standing constraint on this whole adapter and the reason it decides nothing.
///
/// It translates and invokes. Which of these to call, in what order, on which host, and what
/// to do when one fails, is the Application layer's, where it can be exercised against the
/// fake without Windows.
internal static class WmiFailover
{
    private const string Namespace = @"root\virtualization\v2";

    private const string InitiateFailover = "InitiateFailover";
    private const string RevertFailover = "RevertFailover";
    private const string ReverseRelationship = "ReverseReplicationRelationship";
    private const string ModifySystemSettings = "ModifySystemSettings";

    /// `Msvm_ComputerSystem.RequestStateChange` values. 2 is Enabled — the CIM spelling of
    /// "start". 3 is Disabled, which is a **hard power off** and is deliberately never used
    /// here: the graceful path goes through `Msvm_ShutdownComponent`.
    private const ushort Enabled = 2;

    /// A guest the sequence is about to fail over is shut down through its integration
    /// services, never by cutting the power.
    ///
    /// The distinction is the whole reason a planned failover is preferred over an unplanned
    /// one: `RequestStateChange(3)` stops the machine mid-write and leaves the file system
    /// exactly as an unplanned failover would, which is the data loss the operator chose the
    /// planned sequence to avoid. So a guest with no shutdown component is a refusal that
    /// names the problem, not a reason to insist by force.
    public static void ShutDownGracefully(
        CimSession session, CimInstance vm, string vmName, CimOperationOptions options)
    {
        foreach (CimInstance component in session.EnumerateAssociatedInstances(
            Namespace, vm, "Msvm_SystemDevice", "Msvm_ShutdownComponent",
            sourceRole: "GroupComponent", resultRole: "PartComponent", options))
        {
            using (component)
            {
                using CimMethodParametersCollection parameters =
                [
                    CimMethodParameter.Create("Force", false, CimType.Boolean, CimFlags.In),
                    CimMethodParameter.Create(
                        "Reason", "Ripcord planned failover", CimType.String, CimFlags.In),
                ];

                using CimMethodResult result = session.InvokeMethod(
                    Namespace, component, "InitiateShutdown", parameters, options);

                WmiJob.Complete(session, result, options, "InitiateShutdown");
                return;
            }
        }

        throw new InvalidOperationException(
            $"'{vmName}' has no shutdown component, so it cannot be shut down from the host — "
                + "the guest integration services are missing or not running. Shut it down "
                + "from inside the guest and run this again; Ripcord will not force the power "
                + "off, because that loses exactly what a planned failover is chosen to keep.");
    }

    /// `Start-VMFailover -Prepare`, run on the host that still holds the primary copy.
    ///
    /// **The least certain call in this file** (V37), and the reason is worth stating rather
    /// than hiding: the reference lists **no method that means "prepare"**. The whole
    /// `Msvm_ReplicationService` member list was read, and planned-failover preparation is not
    /// among them, so the only available reading is that `InitiateFailover` against the
    /// primary's own computer system is what the cmdlet's `-Prepare` maps to.
    ///
    /// That is an inference, not a documented fact. What makes it tolerable rather than a
    /// guess of the kind this project has been bitten by: the failure is loud. Every documented
    /// return value other than 0 and 4096 is an error — 32775 is "Invalid state for this
    /// operation" — and `WmiJob.Complete` throws on all of them. A wrong reading fails the step
    /// and rolls back; it cannot quietly half-succeed.
    public static void Prepare(
        CimSession session, CimInstance vm, CimOperationOptions options) =>
        Failover(session, vm, options);

    /// `Start-VMFailover` on the replica.
    ///
    /// `SnapshotSettingData` is passed explicitly as null, which the reference documents as
    /// "the failover is to be performed to the latest point in time". It is an `[in]` parameter
    /// of the method rather than an optional extra, so omitting it altogether risks 32773,
    /// "Invalid parameter". Where recovery history is configured, choosing a point is the
    /// caller's to add here (Q1).
    public static void Failover(
        CimSession session, CimInstance vm, CimOperationOptions options)
    {
        using CimMethodParametersCollection parameters =
        [
            CimMethodParameter.Create("ComputerSystem", vm, CimType.Reference, CimFlags.In),
            CimMethodParameter.Create(
                "SnapshotSettingData", null, CimType.Reference, CimFlags.In),
        ];

        Invoke(session, parameters, InitiateFailover, options);
    }

    /// `Set-VMReplication -Reverse`.
    ///
    /// `ReplicationSettingData` is declared in the MOF as a **string**, not a reference — it is
    /// "a string representation of an instance of the Msvm_ReplicationSettingData class". Null
    /// is passed to mean "keep the settings already configured", which is the reading that
    /// matches the cmdlet's behaviour but is **not** stated in the reference (V37). If the
    /// method rejects it, the failure is a documented error code rather than a silent
    /// misconfiguration of the reversed relationship.
    public static void Reverse(
        CimSession session, CimInstance vm, CimOperationOptions options)
    {
        using CimMethodParametersCollection parameters =
        [
            CimMethodParameter.Create("ComputerSystem", vm, CimType.Reference, CimFlags.In),
            CimMethodParameter.Create(
                "ReplicationSettingData", null, CimType.String, CimFlags.In),
        ];

        Invoke(session, parameters, ReverseRelationship, options);
    }

    /// `Stop-VMFailover`. Three behaviours, no discriminating parameter, one of them
    /// destructive — which is why `StopFailoverIntent` resolves the effect before anything
    /// calls this. The adapter invokes; it does not work out which situation it is in.
    public static void Cancel(
        CimSession session, CimInstance vm, CimOperationOptions options)
    {
        using CimMethodParametersCollection parameters =
        [
            CimMethodParameter.Create("ComputerSystem", vm, CimType.Reference, CimFlags.In),
        ];

        Invoke(session, parameters, RevertFailover, options);
    }

    /// Starts a VM for real. Same `RequestStateChange` a test VM uses, against a computer
    /// system that is not a copy.
    public static void Start(
        CimSession session, CimInstance vm, CimOperationOptions options)
    {
        using CimMethodParametersCollection parameters =
        [
            CimMethodParameter.Create("RequestedState", Enabled, CimType.UInt16, CimFlags.In),
        ];

        using CimMethodResult result = session.InvokeMethod(
            Namespace, vm, "RequestStateChange", parameters, options);

        WmiJob.Complete(session, result, options, "RequestStateChange");
    }

    /// Fencing's one mutating call: `Msvm_VirtualSystemManagementService.ModifySystemSettings`
    /// with the VM's realized `Msvm_VirtualSystemSettingData`, its `AutomaticStartupAction`
    /// changed and nothing else touched.
    ///
    /// **Unverified on this hardware (V43).** The MOF declares `SystemSettings` as a string —
    /// "a string representation of an instance of the Msvm_VirtualSystemSettingData class".
    /// The field test refuted passing the instance itself for the same kind of parameter
    /// (V38), so it goes across as its embedded-instance text.
    ///
    /// Whatever it turns out to be, the failure is loud: `ModifySystemSettings` returns a
    /// non-zero code and `WmiJob.Complete` throws, so the fence reports that it did not fence.
    public static void SetStartAction(
        CimSession session,
        CimInstance vm,
        AutomaticStartAction action,
        CimOperationOptions options)
    {
        using CimInstance? settings = WmiVmInventory.MutableSettings(session, vm, options);

        if (settings is null)
        {
            throw new InvalidOperationException(
                "this VM's system settings could not be read, so its startup action cannot be "
                    + "changed — it may still boot itself when this host restarts");
        }

        settings.CimInstanceProperties["AutomaticStartupAction"].Value = (ushort)action;

        using CimMethodParametersCollection parameters =
        [
            CimMethodParameter.Create(
                "SystemSettings",
                CimEmbeddedInstance.Text(settings),
                CimType.String,
                CimFlags.In),
        ];

        using CimInstance service = session.QueryInstances(
            Namespace, "WQL", "SELECT * FROM Msvm_VirtualSystemManagementService", options)
            .First();

        using CimMethodResult result = session.InvokeMethod(
            Namespace, service, ModifySystemSettings, parameters, options);

        WmiJob.Complete(session, result, options, ModifySystemSettings);
    }

    /// The three replication methods hang off the service and differ only in their parameter
    /// lists, which the callers build — the reference gives each a different signature, and a
    /// shared list was how this file first got two of them wrong.
    private static void Invoke(
        CimSession session,
        CimMethodParametersCollection parameters,
        string method,
        CimOperationOptions options)
    {
        using CimInstance service = ReplicationService(session, options);

        using CimMethodResult result = session.InvokeMethod(
            Namespace, service, method, parameters, options);

        WmiJob.Complete(session, result, options, method);
    }

    /// One instance per host. Resolved per call rather than held: these run minutes apart
    /// inside one sequence, and a stale handle across a WMI restart is a failure at the worst
    /// possible moment.
    private static CimInstance ReplicationService(
        CimSession session, CimOperationOptions options) =>
        session.QueryInstances(
            Namespace, "WQL", "SELECT * FROM Msvm_ReplicationService", options).First();
}
