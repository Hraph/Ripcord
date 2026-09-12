using Ripcord.Domain.Checks;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Checks;

/// A pair with nothing wrong with it, and the small mutations that make one thing wrong.
/// Every rule test starts from the same healthy pair and breaks a single fact, so a test says
/// only what it is about and a rule that fires on the wrong input is visible immediately.
///
/// The test configuration declares `expected_role: replica`, so the local host is the
/// failover *target* and the peer is the source. That is the layout of `ripcord check` run on
/// the disaster recovery host.
internal static class Pairs
{
    public const string Target = ValidDocument.MachineName;
    public const string Source = ValidDocument.PeerName;
    public const string SwitchName = "vSwitch-PROD";
    public const int VlanId = 10;

    private const long Gb = 1024L * 1024 * 1024;

    private static readonly Dictionary<string, string> Macs = new()
    {
        ["VM-DC-01"] = "00-15-5D-01-02-01",
        ["VM-LEGACY-01"] = "00-15-5D-01-02-02",
        ["VM-BACKUP-01"] = "00-15-5D-01-02-03",
    };

    public static IReadOnlyList<string> Names => [.. Macs.Keys];

    public static PairView Healthy(DateTimeOffset now) =>
        new(TargetHost(now), SourceHost(now), now);

    public static HostState TargetHost(DateTimeOffset now) =>
        new(
            Target,
            [.. Names.Select(name => Vm(name, ReplicationRole.Replica, now))],
            HostReachability.Reachable(),
            new HostFacts(
                12_288,
                [
                    new HostVolume("C:", 80 * Gb, 240 * Gb, false, null),
                    new HostVolume("D:", 500 * Gb, 2000 * Gb, true, true),
                ],
                Certificate(ValidDocument.LocalThumbprint, Target)));

    public static HostState SourceHost(DateTimeOffset now) =>
        new(
            Source,
            [.. Names.Select(name => Vm(name, ReplicationRole.Primary, now))],
            HostReachability.Reachable(),
            new HostFacts(
                49_152,
                [new HostVolume("D:", 1200 * Gb, 4000 * Gb, false, null)],
                Certificate(ValidDocument.PeerThumbprint, Source)));

    /// Expiring well past every window the rules use, so the certificate rule is silent
    /// unless a test moves the date.
    public static CertificateFact Certificate(string thumbprint, string hostName) =>
        new(thumbprint, $"CN={hostName}", new DateTimeOffset(2029, 9, 1, 0, 0, 0, TimeSpan.Zero));

    public static CheckReport Evaluate(
        PairView view,
        DateTimeOffset now,
        Action<ConfigurationDocument>? adjust = null,
        IReadOnlyList<string>? notes = null) =>
        CheckEngine.Evaluate(new CheckRequest(
            view, Configurations.Create(adjust), now, notes ?? []));

    public static IEnumerable<Finding> For(this CheckReport report, string ruleId) =>
        report.Findings.Where(finding => finding.Rule.Id == ruleId);

    public static PairView WithTarget(
        this PairView view, string name, Func<VmFacts, VmFacts> change) =>
        view with { Local = Mutate(view.Local, name, change) };

    public static PairView WithSource(
        this PairView view, string name, Func<VmFacts, VmFacts> change) =>
        view with { Peer = Mutate(view.Peer, name, change) };

    public static PairView WithTargetAdapter(
        this PairView view, string name, Func<VirtualAdapter, VirtualAdapter> change) =>
        view.WithTarget(name, facts => facts with
        {
            Adapters = [.. facts.Adapters.Select(change)],
        });

    public static PairView WithSourceAdapter(
        this PairView view, string name, Func<VirtualAdapter, VirtualAdapter> change) =>
        view.WithSource(name, facts => facts with
        {
            Adapters = [.. facts.Adapters.Select(change)],
        });

    public static PairView WithTargetHost(
        this PairView view, Func<HostFacts, HostFacts> change) =>
        view with { Local = view.Local with { Facts = change(view.Local.Facts!) } };

    public static PairView WithSourceHost(
        this PairView view, Func<HostFacts, HostFacts> change) =>
        view with { Peer = view.Peer with { Facts = change(view.Peer.Facts!) } };

    public static PairView WithTargetVm(
        this PairView view, string name, Func<VmReplicationState, VmReplicationState> change) =>
        view with { Local = Replace(view.Local, name, change) };

    public static PairView WithSourceVm(
        this PairView view, string name, Func<VmReplicationState, VmReplicationState> change) =>
        view with { Peer = Replace(view.Peer, name, change) };

    public static PairView WithoutTargetVm(this PairView view, string name) =>
        view with
        {
            Local = view.Local with
            {
                Vms = [.. view.Local.Vms.Where(vm => vm.Name != name)],
            },
        };

    public static PairView WithoutSourceVm(this PairView view, string name) =>
        view with
        {
            Peer = view.Peer with
            {
                Vms = [.. view.Peer.Vms.Where(vm => vm.Name != name)],
            },
        };

    public static PairView WithUnreachableSource(this PairView view, DateTimeOffset since) =>
        view with { Peer = HostState.Unreachable(Source, HostReachability.TimedOut(since)) };

    public static PairView WithVolume(
        this PairView view, Func<HostVolume, HostVolume> change) =>
        view.WithTargetHost(facts => facts with
        {
            Volumes =
            [
                .. facts.Volumes.Select(volume =>
                    volume.Name == "D:" ? change(volume) : volume),
            ],
        });

    private static HostState Mutate(
        HostState host, string name, Func<VmFacts, VmFacts> change) =>
        Replace(host, name, vm => vm with { Facts = change(vm.Facts!) });

    private static HostState Replace(
        HostState host, string name, Func<VmReplicationState, VmReplicationState> change) =>
        host with
        {
            Vms = [.. host.Vms.Select(vm => vm.Name == name ? change(vm) : vm)],
        };

    private static VmReplicationState Vm(
        string name, ReplicationRole role, DateTimeOffset now) =>
        new(
            name,
            role,
            ReplicationState.Replicating,
            ReplicationHealth.Normal,
            now.AddSeconds(-20),
            0,
            new VmFacts(
                2048,
                4096,
                1024,
                [
                    new VirtualAdapter(
                        "Network Adapter", SwitchName, true, Macs[name], false, VlanId),
                ],
                [new VmDisk(Vhdx(name), false)],
                [Vhdx(name)]));

    public static string Vhdx(string name) => $@"D:\VMs\{name}\os.vhdx";
}
