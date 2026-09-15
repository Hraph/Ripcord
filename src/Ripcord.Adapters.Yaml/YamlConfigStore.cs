using Ripcord.Domain.Configuration;
using Ripcord.Ports.Configuration;
using Ripcord.Ports;
using YamlDotNet.Core;
using YamlDotNet.Serialization.NamingConventions;
using YamlDotNet.Serialization;

namespace Ripcord.Adapters.Yaml;

/// Reads `ripcord.yaml` into a document and stops there. Anything it reports is a reason the
/// file could not be read at all; every rule about the contents lives in the Domain.
public sealed class YamlConfigStore : IConfigStore
{
    /// Sections a later milestone owns are in the shipped sample but not in the document.
    /// Ignoring them keeps the sample usable; a misspelt key this milestone does read still
    /// surfaces as a missing required field.
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public ConfigurationRead Read(string path)
    {
        string yaml;

        try
        {
            yaml = File.ReadAllText(path);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Reported as absence rather than as a read failure. The .NET sentence for a file
            // that is not there names the path a second time and says nothing about what to
            // do; what the operator needs is the one command that creates one.
            return ConfigurationRead.Absent(path, "there is no configuration file here");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ConfigurationRead.Failed(path, $"cannot read '{path}': {exception.Message}");
        }

        try
        {
            // A file of comments deserialises to null: no document, and no error either —
            // "the file is empty" is the validator's sentence to write.
            return new ConfigurationRead(Deserializer.Deserialize<ConfigurationDocument?>(yaml), []);
        }
        catch (YamlException exception)
        {
            return ConfigurationRead.Failed(
                path,
                $"{path} is not valid YAML at line {exception.Start.Line}, "
                + $"column {exception.Start.Column}: {InnermostMessage(exception)}");
        }
    }

    public string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // No file, or one that cannot be read. Either way there is nothing to carry over,
            // and `Write` still keeps whatever is on disk before replacing it.
            return null;
        }
    }

    /// The previous file is moved, not deleted, and moved before the new one is written: a
    /// host that loses power between the two steps still has the configuration it had.
    public ConfigurationWrite Write(string path, string content, string keepAs)
    {
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            // Written whole, somewhere else, before anything is moved. The old order — move
            // the previous file aside, then write — leaves the host with no configuration at
            // all when the write is what fails, and says "cannot write" without mentioning
            // that the file it is talking about is no longer there.
            string staged = path + ".new";
            File.WriteAllText(staged, content);

            string? kept = File.Exists(path) ? keepAs : null;

            if (kept is not null)
            {
                File.Move(path, kept, overwrite: true);
            }

            // A rename within one directory, after the bytes are already on disk.
            File.Move(staged, path, overwrite: true);

            return ConfigurationWrite.Succeeded(kept);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException)
        {
            return ConfigurationWrite.Failed($"cannot write '{path}': {exception.Message}");
        }
    }

    /// YamlDotNet wraps a type-conversion failure; the inner message is the one that names
    /// the offending value.
    private static string InnermostMessage(Exception exception) =>
        exception.InnerException is { } inner ? InnermostMessage(inner) : exception.Message;
}
