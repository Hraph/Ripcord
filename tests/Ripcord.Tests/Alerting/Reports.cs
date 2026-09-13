using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Alerting;

/// Check reports built by hand rather than evaluated from a pair. The alerting tests are
/// about which findings are in the report and nothing else, so the rules that produced them
/// are beside the point — and a report assembled here cannot drift when a rule changes.
internal static class Reports
{
    private static readonly DateTimeOffset EvaluatedAt =
        new(2026, 9, 13, 12, 0, 0, TimeSpan.FromHours(2));

    public static CheckReport Clean() => Of();

    public static CheckReport WithSwitchMismatch() => Of(SwitchMismatch());

    public static CheckReport WithSwitchMismatchAndInvertedDirection() =>
        Of(SwitchMismatch(), InvertedDirection());

    public static CheckReport WithWarning() =>
        Of(new Finding(
            CheckRules.ById(CheckRules.LagBeyondThreshold)!,
            "VM-DC-01",
            "lag is 4 minutes",
            "the replica would be four minutes behind",
            null,
            FindingVerdict.Violated));

    public static CheckReport WithAcknowledgedMismatch(DateTimeOffset now) =>
        Of(SwitchMismatch() with
        {
            Suppression = new Suppression(
                new Acknowledgement(
                    CheckRules.ReplicaSwitchMismatch, "VM-DC-01", "rewiring on Friday",
                    now.AddDays(7)),
                IsActive: true),
        });

    private static Finding SwitchMismatch() =>
        new(
            CheckRules.ById(CheckRules.ReplicaSwitchMismatch)!,
            "VM-DC-01",
            "attached to vSwitch-OLD",
            "this VM would boot with no network on the target",
            "Connect-VMNetworkAdapter -VMName VM-DC-01 -SwitchName vSwitch-PROD",
            FindingVerdict.Violated);

    private static Finding InvertedDirection() =>
        new(
            CheckRules.ById(CheckRules.ReplicationDirectionInverted)!,
            null,
            "HV-REPLICA-01 holds the primary copies",
            "a failover would overwrite the newer copies with the older ones",
            null,
            FindingVerdict.Violated);

    private static CheckReport Of(params Finding[] findings) =>
        new(
            EvaluatedAt,
            OperatingMode.Normal,
            "HV-PRIMARY-01",
            "HV-REPLICA-01",
            true,
            findings,
            new Feasibility(null, null, null, []),
            []);
}
