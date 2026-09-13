namespace Ripcord.Domain.Updates;

/// Whether this host may ask GitHub whether a newer release exists, and whether it may act on
/// the answer. Both off by default and off in the shipped sample: these two machines are meant
/// to have no outbound access at all, so the feature has to be genuinely optional rather than
/// merely skippable.
///
/// Two switches rather than one, because they buy different things. `Check` risks a wrong
/// version number on a console; `Install` lets a release replace a binary that runs with
/// Hyper-V privileges on both hosts of the pair. Granting the first must not grant the second.
public sealed record UpdateSettings(bool Check, bool Install = false)
{
    public static UpdateSettings Disabled() => new(false);
}

/// What the release feed came back with. One of the two fields is set, never both: a lookup
/// that failed must not read as a repository with no releases.
public sealed record ReleaseLookup(string? Version, string? FailureMessage)
{
    public static ReleaseLookup Found(string version) => new(version, null);

    public static ReleaseLookup Failed(string message) => new(null, message);
}
