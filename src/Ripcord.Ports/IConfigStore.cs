using Ripcord.Domain.Configuration;

namespace Ripcord.Ports;

/// Reads `ripcord.yaml` into a document. It translates; it does not decide — a missing file
/// or a syntax error is a read error, and everything beyond that is
/// `ConfigurationValidator`'s to judge.
public interface IConfigStore
{
    ConfigurationRead Read(string path);
}

/// Either a document or the reasons none could be read. Errors use the same shape as
/// validation errors so the CLI prints one list.
public sealed record ConfigurationRead(
    ConfigurationDocument? Document,
    IReadOnlyList<ConfigurationError> Errors)
{
    public static ConfigurationRead Succeeded(ConfigurationDocument document) =>
        new(document, []);

    public static ConfigurationRead Failed(string path, string message) =>
        new(null, [new ConfigurationError(path, message)]);
}
