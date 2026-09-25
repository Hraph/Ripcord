using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Configuration;

/// The shipped samples, and the same files with the two certificate thumbprints filled in —
/// the one edit the validator insists on before a sample describes a real host.
internal static class Samples
{
    public static string Read(string fileName) =>
        File.ReadAllText(Path.Combine(Architecture.RepositoryLayout.Root, "config", fileName));

    public static string Filled(string yaml) =>
        yaml.Replace(ListenerSettings.SamplePlaceholders[0], ValidDocument.LocalThumbprint, StringComparison.Ordinal)
            .Replace(ListenerSettings.SamplePlaceholders[1], ValidDocument.PeerThumbprint, StringComparison.Ordinal);
}
