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

    /// A choice, and already said: the PEER section reads "no peer channel configured on this
    /// node". A block on every run of a local-only node would teach the eye to skip it.
    [Fact]
    public void A_disabled_listener_raises_nothing_whatever_the_service()
    {
        Assert.Null(Judge(Enabled with { Enabled = false }, Service(ServiceRunState.Stopped)));
        Assert.Null(Judge(Enabled with { Enabled = false }, ObservedService.Absent));
    }

    /// Same words as `ripcord service`, including what to do about a denied read.
    [Fact]
    public void An_unreadable_service_says_what_ripcord_service_says()
    {
        ObservedService unreadable = new(
            true, null, ServiceRunState.Unknown, null, null, null, ObservedService.AccessDenied);

        Assert.Equal(
            ServiceDiagnosis.Diagnose(RipcordService.Listener, unreadable, null, null).Why,
            Judge(Enabled, unreadable)!.Reason);
    }

    /// The other half: when nothing is wrong, the line says so rather than leaving silence.
    [Theory]
    [InlineData(ServiceRunState.Running, false)]
    [InlineData(ServiceRunState.StartPending, true)]
    public void A_listener_the_peer_can_read_is_said(ServiceRunState state, bool starting)
    {
        RunningBuild build = new("0.7.0+def5678", null, Outdated: true);

        Assert.Equal(
            new ListenerRunning(starting, "0.7.0+def5678", Outdated: true),
            ListenerAvailability.Running(Enabled, Service(state), build));
    }

    /// Exactly when `Judge` raises something, or the listener is switched off, there is no line.
    [Fact]
    public void Nothing_is_said_running_when_it_is_not()
    {
        Assert.Null(ListenerAvailability.Running(Enabled, Service(ServiceRunState.Stopped), null));
        Assert.Null(ListenerAvailability.Running(Enabled, Service(ServiceRunState.Unknown), null));
        Assert.Null(ListenerAvailability.Running(Enabled, ObservedService.Absent, null));
        Assert.Null(ListenerAvailability.Running(
            Enabled with { Enabled = false }, Service(ServiceRunState.Running), null));
    }

    [Fact]
    public void An_unrecorded_build_still_says_running() =>
        Assert.Equal(
            new ListenerRunning(false, null, false),
            ListenerAvailability.Running(Enabled, Service(ServiceRunState.Running), null));

    private static readonly ListenerSettings Enabled =
        new(true, ListenerSettings.DefaultPort, "AA", "BB", @"C:\Program Files\Ripcord\state.json");

    private static ObservedService Service(ServiceRunState state) =>
        new(true, "\"C:\\Program Files\\Ripcord\\ripcord.exe\" serve", state, "Auto", 0, 0);

    private static ListenerAlert? Judge(ListenerSettings listener, ObservedService service) =>
        ListenerAvailability.Judge(listener, Peer, service);
}
