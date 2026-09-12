namespace Ripcord.Domain;

/// Time since an instant, clamped at zero. The two hosts' clocks drift, and a timestamp from
/// the future is skew, not a negative duration.
public static class Elapsed
{
    public static TimeSpan? Between(DateTimeOffset? start, DateTimeOffset now) =>
        start is { } from
            ? now - from is { Ticks: > 0 } elapsed ? elapsed : TimeSpan.Zero
            : null;
}
