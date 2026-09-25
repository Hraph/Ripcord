using Ripcord.Domain.Configuration;
using Ripcord.Domain.Deployment;

namespace Ripcord.Tests.Deployment;

/// Why the other host cannot read this one, said at the bottom of `ripcord status`: a peer
/// shown SILENT over there reads as a network fault unless this side says otherwise.
public sealed class ListenerAvailabilityTests
{
    private const string Peer = "HV-DR-01";

    [Fact]
    public void A_running_listener_raises_nothing()
    {
        Assert.Null(Judge(Enabled, Service(ServiceRunState.Running)));
    }

    [Fact]
    public void A_starting_listener_raises_nothing()
    {
        Assert.Null(Judge(Enabled, Service(ServiceRunState.StartPending)));
    }

    [Fact]
    public void A_stopped_listener_names_the_peer_and_points_to_the_report()
    {
        ListenerAlert alert = Judge(Enabled, Service(ServiceRunState.Stopped))!;

        Assert.Equal("NOT RUNNING", alert.Headline);
        Assert.Contains(Peer, alert.Reason, StringComparison.Ordinal);
        Assert.Contains("stopped", alert.Reason, StringComparison.Ordinal);
        Assert.Equal("ripcord service", alert.Next);
        Assert.True(alert.Critical);
    }

    [Fact]
    public void A_missing_service_points_to_the_install()
    {
        ListenerAlert alert = Judge(Enabled, ObservedService.Absent)!;

        Assert.Equal("NOT INSTALLED", alert.Headline);
        Assert.Equal("ripcord service install --dry-run", alert.Next);
        Assert.True(alert.Critical);
    }

    [Fact]
    public void A_paused_or_stopping_listener_is_not_running_either()
    {
        Assert.Equal("NOT RUNNING", Judge(Enabled, Service(ServiceRunState.Paused))!.Headline);
        Assert.Equal("NOT RUNNING", Judge(Enabled, Service(ServiceRunState.StopPending))!.Headline);
    }

    /// Never "stopped" when Windows did not say: that would send somebody to restart a
    /// listener that may be serving.
    [Fact]
    public void An_unreadable_service_is_cannot_tell_with_the_reason()
    {
        ObservedService unreadable = new(
            true, null, ServiceRunState.Unknown, null, null, null, ObservedService.AccessDenied);

        ListenerAlert alert = Judge(Enabled, unreadable)!;

        Assert.Equal("UNKNOWN", alert.Headline);
        Assert.Contains(ObservedService.AccessDenied, alert.Reason, StringComparison.Ordinal);
        Assert.False(alert.Critical);
    }

    [Fact]
    public void A_disabled_listener_says_the_channel_is_off_whatever_the_service()
    {
        ListenerAlert alert = Judge(Enabled with { Enabled = false }, Service(ServiceRunState.Running))!;

        Assert.Equal("OFF", alert.Headline);
        Assert.Contains(Peer, alert.Reason, StringComparison.Ordinal);
        Assert.False(alert.Critical);
    }

    private static readonly ListenerSettings Enabled =
        new(true, ListenerSettings.DefaultPort, "AA", "BB", @"C:\Program Files\Ripcord\state.json");

    private static ObservedService Service(ServiceRunState state) =>
        new(true, "\"C:\\Program Files\\Ripcord\\ripcord.exe\" serve", state, "Auto", 0, 0);

    private static ListenerAlert? Judge(ListenerSettings listener, ObservedService service) =>
        ListenerAvailability.Judge(listener, Peer, service);
}
