using Ripcord.Domain.Configuration;

namespace Ripcord.Ports.Configuration;

/// Reads `ripcord.yaml` into a document, and writes one back. It translates; it does not
/// decide — a missing file or a syntax error is a read error, and everything beyond that is
/// `ConfigurationValidator`'s to judge.
public interface IConfigStore
{
    ConfigurationRead Read(string path);

    /// The file as it stands, line for line. `ripcord init` carries every section it does not
    /// ask about across untouched, and that can only be done from the text: the document model
    /// ignores keys it does not know and holds no comments at all.
    ///
    /// Null when there is no file, which is not an error — it is the first install.
    string? ReadText(string path);

    /// Writes the configuration, keeping whatever was there as `keepAs` first.
    ///
    /// The previous file is moved rather than deleted, and that is what makes `ripcord init`
    /// safe to re-run: nothing it does is unrecoverable, so it needs no typed confirmation to
    /// be honest about its consequences.
    ConfigurationWrite Write(string path, string content, string keepAs);
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

    /// No file at all, which is a different situation from a file that is wrong: one is
    /// repaired by editing and the other by creating, and they used to say the same sentence.
    public static ConfigurationRead Absent(string path, string message) =>
        new(null, [new ConfigurationError(path, message, ConfigurationErrorKind.Missing)]);
}

/// What the filesystem did. `Kept` names the file the previous configuration was moved to, or
/// is null when there was nothing to keep.
public sealed record ConfigurationWrite(bool Written, string? Kept, string? FailureMessage)
{
    public static ConfigurationWrite Succeeded(string? kept) => new(true, kept, null);

    public static ConfigurationWrite Failed(string message) => new(false, null, message);
}
