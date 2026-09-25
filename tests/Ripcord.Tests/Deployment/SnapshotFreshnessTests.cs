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
}
