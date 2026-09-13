using Ripcord.Domain.Configuration;

namespace Ripcord.Domain.Failover;

/// Which VMs an invocation was asked to act on. Three forms, never combined: the CLI settles
/// that, because `--all --vm VM-DC-01` has no reading that is obviously right and guessing at
/// one moves production.
public sealed record SweepScope
{
    private SweepScope(IReadOnlyList<string> named, VmPriority? priority, bool all)
    {
        this.NamedVms = named;
        this.Priority = priority;
        this.IsAll = all;
    }

    public static SweepScope All { get; } = new([], null, true);

    public IReadOnlyList<string> NamedVms { get; }

    public VmPriority? Priority { get; }

    public bool IsAll { get; }

    public static SweepScope Named(IReadOnlyList<string> names) =>
        new(names ?? [], null, false);

    public static SweepScope OfPriority(VmPriority priority) => new([], priority, false);
}

/// A VM the sweep left alone, and why. Carried rather than dropped: an operator who typed
/// `--all` has to learn on the run itself which machine was not part of "all".
public sealed record SweepExclusion(string VmName, string Reason);

/// What the sweep will act on, what it left alone, and what stops it outright.
///
/// `Excluded` and `Refusals` are different things on purpose. An excluded VM is the
/// configuration working as written — decision D19's whole point is that a machine can be kept
/// out of `--all` without being kept out of the tool. A refusal is the operator having asked
/// for something that must not happen, and then nothing happens at all.
public sealed record SweepSelection(
    IReadOnlyList<string> VmNames,
    IReadOnlyList<SweepExclusion> Excluded,
    IReadOnlyList<string> Refusals)
{
    public bool Refuses => this.Refusals.Count > 0;

    public bool Equals(SweepSelection? other) =>
        other is not null
        && Structural.Same(this.VmNames, other.VmNames)
        && Structural.Same(this.Excluded, other.Excluded)
        && Structural.Same(this.Refusals, other.Refusals);

    public override int GetHashCode()
    {
        HashCode hash = new();
        Structural.Add(ref hash, this.VmNames);
        Structural.Add(ref hash, this.Excluded);
        Structural.Add(ref hash, this.Refusals);
        return hash.ToHashCode();
    }
}

/// Turns "what the operator typed" into "which VMs, in what order".
///
/// Ordering is by priority and never by the order of the arguments: P1 exists to say the
/// domain controller comes back first, and an operator naming three machines is asking for a
/// failover rather than for their typing order.
public static class FailoverSweep
{
    public static SweepSelection Select(IReadOnlyList<VmSettings> vms, SweepScope scope)
    {
        ArgumentNullException.ThrowIfNull(vms);
        ArgumentNullException.ThrowIfNull(scope);

        return scope.NamedVms.Count > 0 || (!scope.IsAll && scope.Priority is null)
            ? Named(vms, scope.NamedVms)
            : Swept(vms, scope);
    }

    /// Naming a VM overrides `manual` — that is what `manual` means — but not `never`, and an
    /// unknown name stops everything: failing over two of the three machines somebody asked
    /// for, because the third was a typo, is the failure mode this exists to prevent.
    private static SweepSelection Named(
        IReadOnlyList<VmSettings> vms, IReadOnlyList<string> names)
    {
        List<string> refusals = [];
        List<VmSettings> chosen = [];

        foreach (string name in names)
        {
            VmSettings? vm = vms.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));

            if (vm is null)
            {
                refusals.Add($"'{name}' is not a VM in this configuration");
                continue;
            }

            if (vm.Failover == FailoverPolicy.Never)
            {
                refusals.Add(
                    $"'{vm.Name}' is failover: never in this configuration and will not be "
                        + "failed over by Ripcord");
                continue;
            }

            chosen.Add(vm);
        }

        if (names.Count == 0)
        {
            refusals.Add("no VM was named");
        }

        return new SweepSelection(
            refusals.Count > 0 ? [] : InOrder(chosen), [], refusals);
    }

    private static SweepSelection Swept(IReadOnlyList<VmSettings> vms, SweepScope scope)
    {
        List<VmSettings> inScope =
        [
            .. vms.Where(vm => scope.Priority is not { } tier || vm.Priority == tier),
        ];

        List<SweepExclusion> excluded =
        [
            .. inScope
                .Where(vm => vm.Failover != FailoverPolicy.Auto)
                .OrderBy(vm => vm.Priority)
                .Select(vm => new SweepExclusion(vm.Name, ReasonFor(vm))),
        ];

        IReadOnlyList<string> chosen =
            InOrder(inScope.Where(vm => vm.Failover == FailoverPolicy.Auto));

        // Exiting 0 having done nothing is the worst answer available: it reads as "production
        // has moved".
        List<string> refusals = chosen.Count > 0
            ? []
            : [scope.Priority is { } only
                ? $"no {only} VM in this configuration is swept: name one to fail it over"
                : "no VM in this configuration is swept: name one to fail it over"];

        return new SweepSelection(chosen, excluded, refusals);
    }

    private static string ReasonFor(VmSettings vm) =>
        vm.Failover == FailoverPolicy.Manual
            ? "failover: manual — it is excluded from sweeps and has to be named"
            : "failover: never — it is not failed over by Ripcord";

    private static IReadOnlyList<string> InOrder(IEnumerable<VmSettings> vms) =>
        [.. vms.OrderBy(vm => vm.Priority).Select(vm => vm.Name)];
}
