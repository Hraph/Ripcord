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

    /// YamlDotNet wraps a type-conversion failure; the inner message is the one that names
    /// the offending value.
    private static string InnermostMessage(Exception exception) =>
        exception.InnerException is { } inner ? InnermostMessage(inner) : exception.Message;
}
