using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Deployment;

public class SnapshotFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_file_is_missing() =>
        Assert.Equal(
            SnapshotAge.Missing,
            SnapshotFreshness.Judge(null, Now, TimeSpan.FromMinutes(2)));

    [Fact]
    public void Older_than_the_threshold_is_stale() =>
        Assert.Equal(
            SnapshotAge.Stale,
            SnapshotFreshness.Judge(Now.AddMinutes(-3), Now, TimeSpan.FromMinutes(2)));

    [Fact]
    public void At_the_threshold_is_still_fresh() =>
        Assert.Equal(
            SnapshotAge.Fresh,
            SnapshotFreshness.Judge(Now.AddMinutes(-2), Now, TimeSpan.FromMinutes(2)));

    [Fact]
    public void Without_a_threshold_only_a_missing_file_is_reported() =>
        Assert.Equal(SnapshotAge.Fresh, SnapshotFreshness.Judge(Now.AddDays(-9), Now, null));

    /// The next command comes from the publisher, the one thing meant to keep the snapshot
    /// current — never `ripcord status`, which only refreshes it once.
    [Theory]
    [InlineData(false, ServiceRunState.Stopped, "ripcord service install")]
    [InlineData(true, ServiceRunState.Stopped, "ripcord service start")]
    [InlineData(true, ServiceRunState.Paused, "ripcord service start")]
    [InlineData(true, ServiceRunState.Running, "ripcord publish")]
    [InlineData(true, ServiceRunState.StartPending, "ripcord publish")]
    [InlineData(true, ServiceRunState.Unknown, "ripcord service")]
    public void A_stale_snapshot_names_what_the_publisher_needs(
        bool installed, ServiceRunState state, string next)
    {
        ObservedService publisher = new(installed, null, state, "Auto", null, null);

        Assert.Equal(next, SnapshotFreshness.Remedy(SnapshotAge.Stale, publisher)!.Next);
        Assert.Equal(next, SnapshotFreshness.Remedy(SnapshotAge.Missing, publisher)!.Next);
    }

    [Fact]
    public void A_current_snapshot_needs_nothing()
    {
        Assert.Null(SnapshotFreshness.Remedy(SnapshotAge.Fresh, null));
        Assert.Null(SnapshotFreshness.Remedy(null, null));
        Assert.Equal("ripcord service install", SnapshotFreshness.Remedy(SnapshotAge.Missing, null)!.Next);
    }
}
