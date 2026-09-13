using System.Text.Json;
using Ripcord.Domain.Alerting;
using Ripcord.Ports.Alerting;

namespace Ripcord.Adapters.Notify;

/// What the last run said, on disk. One small JSON object beside the binary, holding no
/// secret and meant to be readable by eye: after a quiet night the question is "what did it
/// think was wrong", and the answer has to be in the file rather than behind a hash.
public sealed class FileAlertStateStore(string path) : IAlertStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    /// Never throws: no file, an unreadable one and an unparseable one all mean "first run".
    /// The cost is one duplicate notification; the alternative is a `ripcord check` that
    /// fails over its own bookkeeping.
    public AlertState Read()
    {
        try
        {
            return JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Options)?.ToState()
                ?? AlertState.Clear;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return AlertState.Clear;
        }
    }

    /// Written aside and moved into place: a host that loses power here must leave either the
    /// old record or the new one, never half of either.
    public void Write(AlertState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Stored.From(state), Options));
        File.Move(temporary, path, overwrite: true);
    }

    /// The findings as a list rather than as one newline-joined string: the file is read by a
    /// human, and the Domain's fingerprint is an implementation detail of the comparison.
    private sealed record Stored
    {
        public IReadOnlyList<string> Findings { get; init; } = [];

        public DateTimeOffset? NotifiedAt { get; init; }

        public DateTimeOffset? HeldSince { get; init; }

        public static Stored From(AlertState state) => new()
        {
            Findings = state.Fingerprint.Length == 0 ? [] : [.. state.Fingerprint.Split('\n')],
            NotifiedAt = state.NotifiedAt,
            HeldSince = state.HeldSince,
        };

        public AlertState ToState() =>
            new(string.Join('\n', this.Findings), this.NotifiedAt, this.HeldSince);
    }
}
