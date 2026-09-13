namespace Ripcord.Domain.Failover;

/// One step of a failover sequence, named for what it achieves rather than for the cmdlet it
/// maps to. The adapter knows the cmdlet; the operator reading this at 3 a.m. does not have to.
public enum FailoverAction
{
    ShutDownVm,
    PrepareFailover,
    StartFailover,
    ReverseReplication,
    StartVm,

    /// Not a formality: a VM that boots without network is a failed failover.
    VerifyNetwork,
}

/// A step, and the host it runs on. The host is a field rather than a sentence in the
/// description because every consumer needs it separately — the renderer puts it in a column,
/// and the sequence uses it to decide whether this invocation may carry the step out.
public sealed record FailoverStep(
    int Number, FailoverAction Action, string HostName, string Description)
{
    /// Windows host names are case-insensitive and the configuration was typed by hand.
    public bool RunsOn(string machineName) =>
        string.Equals(this.HostName, machineName, StringComparison.OrdinalIgnoreCase);
}

/// The whole sequence, both hosts' halves, decided before anything is done. `--dry-run` prints
/// exactly this and stops, which is why it is a value rather than a series of calls: the
/// output read before confirming has to be the plan that then runs.
///
/// Ripcord drives only the host it runs on (D29), so the plan deliberately describes steps
/// this invocation cannot carry out. Printing only the local half would hide the fact that the
/// operator has to move to the other host, which is the single thing they most need to know
/// before starting.
public sealed record FailoverPlan(string VmName, IReadOnlyList<FailoverStep> Steps)
{
    /// The planned sequence. Reversal happens here, at step 4, and exactly once — a failback
    /// that reverses again inverts the pair and trips milestone 2's direction rule.
    public static FailoverPlan Planned(string vmName, string primary, string replica) =>
        new(vmName,
        [
            new FailoverStep(1, FailoverAction.ShutDownVm, primary,
                $"Shut down {vmName} so its disks stop changing"),

            new FailoverStep(2, FailoverAction.PrepareFailover, primary,
                "Send the last changes to the replica and mark the failover started"),

            new FailoverStep(3, FailoverAction.StartFailover, replica,
                $"Bring {vmName} up on this host as the live copy"),

            new FailoverStep(4, FailoverAction.ReverseReplication, replica,
                "Turn replication round so the old primary becomes the replica"),

            new FailoverStep(5, FailoverAction.StartVm, replica, $"Start {vmName}"),

            new FailoverStep(6, FailoverAction.VerifyNetwork, replica,
                "Confirm the adapter is connected to the expected switch"),
        ]);

    public bool Equals(FailoverPlan? other) =>
        other is not null
        && this.VmName == other.VmName
        && Structural.Same(this.Steps, other.Steps);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(this.VmName);
        Structural.Add(ref hash, this.Steps);
        return hash.ToHashCode();
    }
}
