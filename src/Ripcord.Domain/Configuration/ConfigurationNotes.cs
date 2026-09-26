namespace Ripcord.Domain.Configuration;

/// What a valid configuration still has to tell whoever reads status or check.
public static class ConfigurationNotes
{
    /// Short enough to fit 75 columns behind "ripcord: ".
    public const string NoVmDeclared =
        "no VM is declared, so nothing would fail over: run ripcord init";

    public const string SnapshotPathIgnored =
        "listener.snapshot_path is no longer used: remove the line";

    public static IReadOnlyList<string> Of(RipcordConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        List<string> notes = [];

        if (configuration.Vms.Count == 0)
        {
            notes.Add(NoVmDeclared);
        }

        if (configuration.Listener.SnapshotPathIgnored)
        {
            notes.Add(SnapshotPathIgnored);
        }

        return notes;
    }
}
