using System.Text.Json;
using Ripcord.Adapters.Audit;
using Ripcord.Domain.Audit;

namespace Ripcord.Tests.Audit;

/// The trail read the morning after. Append-only in shape as well as by ACL, one self-contained
/// line per event, and readable after a host has gone down part-way through writing one.
public class JsonLinesAuditLogTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 3, 1, 9, 0, 0, TimeSpan.FromHours(2));

    private readonly string path = Path.Combine(
        Path.GetTempPath(), $"ripcord-audit-{Guid.NewGuid():N}.jsonl");

    /// The whole point of appending: a second operation must not erase the account of the
    /// first. Rewriting is how a trail stops being evidence.
    [Fact]
    public void A_second_entry_does_not_replace_the_first()
    {
        JsonLinesAuditLog log = new(this.path);

        log.Append(Entry(AuditStage.Starting, "about to fail over"));
        log.Append(Entry(AuditStage.Finished, "exit 0"));

        Assert.Equal(2, File.ReadAllLines(this.path).Length);
    }

    /// Each line stands alone. A document would have to be closed to parse; a host that loses
    /// power mid-failover leaves a truncated last line and every line before it still readable,
    /// which is the case this format is chosen for.
    [Fact]
    public void Every_line_parses_on_its_own()
    {
        JsonLinesAuditLog log = new(this.path);

        log.Append(Entry(AuditStage.Starting, "first"));
        log.Append(Entry(AuditStage.Finished, "second"));

        Assert.All(
            File.ReadAllLines(this.path),
            line => JsonDocument.Parse(line).Dispose());
    }

    /// An audit line is compared across two hosts, so the instant must not depend on either
    /// host's offset. It is written in UTC, in a round-trip format.
    [Fact]
    public void The_instant_is_written_in_utc()
    {
        new JsonLinesAuditLog(this.path).Append(Entry(AuditStage.Starting, "x"));

        Assert.Equal(
            Now.ToUniversalTime(),
            DateTimeOffset.Parse(this.Field("at"), null));
    }

    /// The field that earns this record its place. What the run could not establish has to
    /// survive into the trail, not only into the console.
    [Fact]
    public void The_unverified_facts_survive_into_the_line()
    {
        new JsonLinesAuditLog(this.path).Append(
            Entry(AuditStage.Starting, "x", ["free space could not be read"]));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllLines(this.path)[0]);

        Assert.Equal(
            "free space could not be read",
            document.RootElement.GetProperty("unverified")[0].GetString());
    }

    /// A line is read by eye in a terminal as often as by a parser, so no type names and no
    /// nesting beyond the one array.
    [Fact]
    public void The_line_carries_no_type_names()
    {
        new JsonLinesAuditLog(this.path).Append(Entry(AuditStage.Starting, "x"));

        Assert.DoesNotContain(
            "Ripcord.", File.ReadAllText(this.path), StringComparison.Ordinal);
    }

    [Fact]
    public void The_stage_is_named_rather_than_numbered()
    {
        new JsonLinesAuditLog(this.path).Append(Entry(AuditStage.Starting, "x"));

        Assert.Equal("Starting", this.Field("stage"));
    }

    /// A directory that does not exist, or a file the service cannot write, must throw rather
    /// than swallow — the caller stops the failover on it deliberately.
    [Fact]
    public void An_unwritable_path_throws_rather_than_failing_quietly()
    {
        JsonLinesAuditLog log = new(
            Path.Combine(this.path, "no", "such", "directory", "audit.jsonl"));

        Assert.ThrowsAny<IOException>(() => log.Append(Entry(AuditStage.Starting, "x")));
    }

    public void Dispose()
    {
        if (File.Exists(this.path))
        {
            File.Delete(this.path);
        }

        GC.SuppressFinalize(this);
    }

    private string Field(string name)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllLines(this.path)[0]);

        return document.RootElement.GetProperty(name).GetString()!;
    }

    private static AuditEntry Entry(
        AuditStage stage, string detail, IReadOnlyList<string>? unverified = null) =>
        new(
            Now,
            "RH",
            "HV-PRIMARY-01",
            "failover --scenario planned",
            "VM-DC-01",
            stage,
            detail,
            unverified ?? [],
            "0.4.0+abc123");
}
