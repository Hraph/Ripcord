using System.Globalization;
using Ripcord.Domain.Diagnostics;

namespace Ripcord.Domain.Pairing;

/// What the publishing service writes to its log, decided from each attempt in turn.
///
/// Every fifteen seconds is too often to write each one, and silence cannot tell "working" from
/// "stuck". So: the first result, every change between published and not, a new reason, a
/// change in what the read could not see, one line an hour whatever the state, and the stop.
public sealed record PublishJournal(
    bool Started,
    bool Healthy,
    string? Reason,
    string Notes,
    DateTimeOffset? FailingSince,
    int FailedInARow,
    int Published,
    int Failed,
    DateTimeOffset? LastPublished,
    DateTimeOffset SummaryDue,

    /// How long the attempts since the last summary took. One past `RepublishEvery` ages the
    /// peer's view by the overrun, and can make it read stale on a host that is fine.
    AttemptTimes Times = default)
{
    public const string Operation = "publish";

    public static readonly TimeSpan SummaryEvery = TimeSpan.FromHours(1);

    public static PublishJournal Start { get; } =
        new(false, false, null, "", null, 0, 0, 0, null, DateTimeOffset.MinValue);

    /// `took` is the attempt's own duration, without the wait before the next one.
    public (PublishJournal Next, IReadOnlyList<DiagnosticEntry> Lines) After(
        Publication publication, DateTimeOffset now, TimeSpan took = default)
    {
        ArgumentNullException.ThrowIfNull(publication);

        List<DiagnosticEntry> lines = [];
        bool healthy = publication.Succeeded;
        string reason = publication.Reason ?? publication.Kind.ToString();
        string notes = string.Join("; ", publication.Notes);

        if (!this.Started)
        {
            lines.Add(healthy
                ? Line($"publishing every {Seconds(HostSnapshot.RepublishEvery)} s to {publication.Path}")
                : Line($"not published: {reason}"));
        }
        else if (healthy && !this.Healthy)
        {
            lines.Add(Line(
                $"published again after {Span(now - (this.FailingSince ?? now))}, "
                + $"{this.FailedInARow} attempt(s) failed"));
        }
        else if (!healthy && (this.Healthy || reason != this.Reason))
        {
            lines.Add(Line($"not published: {reason}"));
        }

        if (notes != this.Notes && (this.Started || notes.Length > 0))
        {
            lines.Add(Line(notes.Length > 0 ? $"read with gaps: {notes}" : "read in full again"));
        }

        PublishJournal next = this with
        {
            Started = true,
            Healthy = healthy,
            Reason = healthy ? null : reason,
            Notes = notes,
            FailingSince = healthy ? null : this.Healthy || !this.Started ? now : this.FailingSince,
            FailedInARow = healthy ? 0 : this.FailedInARow + 1,
            Published = this.Published + (healthy ? 1 : 0),
            Failed = this.Failed + (healthy ? 0 : 1),
            LastPublished = healthy ? now : this.LastPublished,
            SummaryDue = this.Started ? this.SummaryDue : now + SummaryEvery,
            Times = took > TimeSpan.Zero ? this.Times.Add(took) : this.Times,
        };

        if (now < next.SummaryDue)
        {
            return (next, lines);
        }

        lines.Add(Line(next.Summary("in the last hour")));

        return (
            next with { Published = 0, Failed = 0, SummaryDue = now + SummaryEvery, Times = default },
            lines);
    }

    /// The last line of a run: what it did since the last summary, so no hour goes unaccounted.
    public IReadOnlyList<DiagnosticEntry> Stopping() =>
        [Line($"stopping: {this.Summary("since the last summary")}")];

    private string Summary(string period) =>
        (this.Healthy
            ? $"{this.Published} snapshot(s) published {period}"
                + (this.LastPublished is { } last ? $", the last at {Clock(last)} UTC" : "")
            : $"{this.Failed} attempt(s) failed {period}"
                + (this.Published > 0 ? $", {this.Published} published" : ""))
        + (this.Times.Count > 0
            ? $", took {Tenths(this.Times.Min)} s min, {Tenths(this.Times.Mean)} s mean, "
                + $"{Tenths(this.Times.Max)} s max"
            : "")
        + (this.Healthy ? "" : $": {this.Reason}");

    private static DiagnosticEntry Line(string message) => DiagnosticEntry.Of(Operation, message);

    private static string Clock(DateTimeOffset at) =>
        at.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Tenths(TimeSpan span) =>
        span.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan span) =>
        ((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    private static string Span(TimeSpan span) =>
        span.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalMinutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalSeconds} s");
}

/// Durations kept as a count and three figures, not a list: an hour is 240 attempts.
public readonly record struct AttemptTimes(int Count, TimeSpan Min, TimeSpan Max, TimeSpan Total)
{
    public TimeSpan Mean => this.Count > 0 ? this.Total / this.Count : TimeSpan.Zero;

    public AttemptTimes Add(TimeSpan took) =>
        this.Count == 0
            ? new(1, took, took, took)
            : new(
                this.Count + 1,
                took < this.Min ? took : this.Min,
                took > this.Max ? took : this.Max,
                this.Total + took);
}
