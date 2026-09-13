using Ripcord.Domain.Alerting;
using Ripcord.Domain.Checks;

namespace Ripcord.Tests.Alerting;

/// What lands on a phone at 3 a.m. One screen, the finding and what it means on the day of
/// the failover — never a dump of the whole check output.
public class NotificationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 13, 12, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void The_subject_names_the_pair_and_counts_the_findings()
    {
        Notification notification = Raised(Reports.WithSwitchMismatchAndInvertedDirection());

        Assert.Equal(
            "ripcord: 2 critical findings on HV-PRIMARY-01 -> HV-REPLICA-01",
            notification.Subject);
    }

    [Fact]
    public void One_finding_is_counted_in_the_singular()
    {
        Assert.Contains(
            "1 critical finding on", Raised(Reports.WithSwitchMismatch()).Subject,
            StringComparison.Ordinal);
    }

    /// The implication is the line that gets somebody out of bed; the observation on its own
    /// says nothing to a person who is not already looking at the pair.
    [Fact]
    public void Each_finding_carries_what_it_means_on_the_day_and_how_to_fix_it()
    {
        string body = Raised(Reports.WithSwitchMismatch()).Body;

        Assert.Contains("VM-DC-01", body, StringComparison.Ordinal);
        Assert.Contains("attached to vSwitch-OLD", body, StringComparison.Ordinal);
        Assert.Contains(
            "this VM would boot with no network on the target", body, StringComparison.Ordinal);
        Assert.Contains("Connect-VMNetworkAdapter", body, StringComparison.Ordinal);
    }

    /// Grouping: everything wrong with the pair in one message, because two messages about
    /// one incident is how an operator learns to read neither.
    [Fact]
    public void Every_finding_is_in_the_one_notification()
    {
        string body = Raised(Reports.WithSwitchMismatchAndInvertedDirection()).Body;

        Assert.Contains("attached to vSwitch-OLD", body, StringComparison.Ordinal);
        Assert.Contains("HV-REPLICA-01 holds the primary copies", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finding_with_no_remedy_says_nothing_about_one()
    {
        string body = Raised(Reports.WithSwitchMismatchAndInvertedDirection()).Body;

        Assert.Equal(1, Occurrences(body, "fix:"));
    }

    [Fact]
    public void The_recovery_notification_says_the_pair_is_clear()
    {
        AlertState raised = AlertPolicy.Decide(
            new AlertRequest(Reports.WithSwitchMismatch(), Settings, AlertState.Clear, Now)).State;

        Notification notification = AlertPolicy.Decide(
            new AlertRequest(Reports.Clean(), Settings, raised, Now.AddHours(1))).Notification!;

        Assert.Equal(AlertKind.Recovered, notification.Kind);
        Assert.Equal("ripcord: HV-REPLICA-01 is clear again", notification.Subject);
        Assert.Contains("no critical rule", notification.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_carrying_a_newline_cannot_forge_a_line_of_its_own()
    {
        Notification notification = Raised(Reports.WithAHostileHostName());

        Assert.DoesNotContain("\nfake: all clear", notification.Body, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', notification.Subject);
    }

    private static readonly AlertingSettings Settings =
        new(true, TimeSpan.FromHours(24), null, null, null);

    private static Notification Raised(CheckReport report) =>
        AlertPolicy.Decide(new AlertRequest(report, Settings, AlertState.Clear, Now))
            .Notification!;

    private static int Occurrences(string text, string needle) =>
        text.Split(needle).Length - 1;
}
