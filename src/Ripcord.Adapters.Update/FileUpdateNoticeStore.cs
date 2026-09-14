using System.Text.Json;
using System.Text.Json.Serialization;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Updates;

namespace Ripcord.Adapters.Update;

/// One small file beside the binary, written by `check-update` and read by every command that
/// renders a header. It holds no secret and nothing an operator needs to edit.
public sealed class FileUpdateNoticeStore(string path) : IUpdateNoticeStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public UpdateNotice? Read()
    {
        try
        {
            Stored? stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Format);

            return stored is { Version.Length: > 0 }
                ? new UpdateNotice(stored.Version, stored.SeenAt)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            // A host that has not looked, which is the honest reading of a file that cannot be
            // read. `ripcord status` must not fail on it.
            return null;
        }
    }

    /// Written whole under another name and moved into place, so a run interrupted here never
    /// leaves a half-written file for the next command to fail to parse.
    public void Write(UpdateNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        string writing = path + ".partial";

        File.WriteAllText(
            writing, JsonSerializer.Serialize(new Stored(notice.Version, notice.SeenAt), Format));

        File.Move(writing, path, overwrite: true);
    }

    private sealed record Stored(string Version, DateTimeOffset SeenAt);
}
