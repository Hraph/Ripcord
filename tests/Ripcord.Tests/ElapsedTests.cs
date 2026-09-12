using Ripcord.Domain.Replication;
using Ripcord.Domain;

namespace Ripcord.Tests;

/// The two hosts' clocks drift. Every elapsed time in Ripcord clamps at zero rather than
/// showing a negative duration, and it does so in one place.
public class ElapsedTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Elapsed_is_the_difference_between_the_two_instants()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), Elapsed.Between(Now.AddMinutes(-5), Now));
    }

    [Fact]
    public void A_start_in_the_future_is_clock_skew_not_negative_elapsed()
    {
        Assert.Equal(TimeSpan.Zero, Elapsed.Between(Now.AddMinutes(3), Now));
    }

    [Fact]
    public void No_start_means_no_elapsed_time()
    {
        Assert.Null(Elapsed.Between(null, Now));
    }
}
