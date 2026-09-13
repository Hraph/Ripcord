using Ripcord.Domain.Dashboard;
using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Configuration;

/// The block that opens a listening socket on a Hyper-V host. It is off unless the file says
/// otherwise, and it has no address key at all: the page is served on the loopback interface
/// by construction, so there is no setting an operator can get wrong.
public class DashboardSettingsValidationTests
{
    [Fact]
    public void A_configuration_with_no_dashboard_block_serves_nothing()
    {
        ConfigurationDocument document = Valid();
        document.Dashboard = null;

        ConfigurationValidation result = Validate(document);

        Assert.Empty(result.Errors);
        Assert.False(result.Configuration!.Dashboard.Enabled);
    }

    /// Present but not switched on is still off. A block written in advance must not start a
    /// socket the day someone upgrades the binary.
    [Fact]
    public void A_dashboard_block_that_does_not_enable_it_serves_nothing()
    {
        ConfigurationDocument document = Valid();
        document.Dashboard = new DashboardDocument();

        ConfigurationValidation result = Validate(document);

        Assert.Empty(result.Errors);
        Assert.False(result.Configuration!.Dashboard.Enabled);
    }

    [Fact]
    public void An_enabled_dashboard_takes_the_defaults_it_was_not_given()
    {
        ConfigurationDocument document = Valid();
        document.Dashboard = new DashboardDocument { Enabled = true };

        ConfigurationValidation result = Validate(document);

        Assert.Empty(result.Errors);
        DashboardSettings dashboard = result.Configuration!.Dashboard;
        Assert.True(dashboard.Enabled);
        Assert.Equal(DashboardSettings.DefaultPort, dashboard.Port);
        Assert.Equal(DashboardSettings.DefaultRefresh, dashboard.Refresh);
    }

    [Fact]
    public void A_declared_port_and_refresh_are_carried_through()
    {
        ConfigurationDocument document = Valid();
        document.Dashboard = new DashboardDocument
        {
            Enabled = true,
            Port = 8081,
            RefreshSec = 60,
        };

        ConfigurationValidation result = Validate(document);

        Assert.Empty(result.Errors);
        Assert.Equal(8081, result.Configuration!.Dashboard.Port);
        Assert.Equal(TimeSpan.FromMinutes(1), result.Configuration.Dashboard.Refresh);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65_536)]
    public void A_port_outside_the_range_is_refused(int port)
    {
        ConfigurationDocument document = Valid();
        document.Dashboard = new DashboardDocument { Enabled = true, Port = port };

        ConfigurationValidation result = Validate(document);

        Assert.Contains(result.Errors, error => error.Path == "dashboard.port");
        Assert.Null(result.Configuration);
    }

    /// Each refresh re-reads Hyper-V. A page set to reload every second would put a host
    /// already under load into a WMI query loop, which is the opposite of what it is for.
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(86_401)]
    public void A_refresh_interval_outside_the_range_is_refused(int seconds)
    {
        ConfigurationDocument document = Valid();
        document.Dashboard = new DashboardDocument { Enabled = true, RefreshSec = seconds };

        ConfigurationValidation result = Validate(document);

        Assert.Contains(result.Errors, error => error.Path == "dashboard.refresh_sec");
        Assert.Null(result.Configuration);
    }

    /// The pair listener is a different socket with a different purpose; sharing a port would
    /// mean one of the two silently failing to bind.
    [Fact]
    public void The_dashboard_may_not_take_the_pair_listener_s_port()
    {
        ConfigurationDocument document = Valid();
        document.Dashboard = new DashboardDocument
        {
            Enabled = true,
            Port = document.Listener!.Port,
        };

        ConfigurationValidation result = Validate(document);

        Assert.Contains(result.Errors, error => error.Path == "dashboard.port");
    }

    /// Refused rather than ignored, the same way a disabled listener's bad port is. A figure
    /// nobody validated until the day it is switched on is a figure that fails on that day.
    [Fact]
    public void A_disabled_dashboard_with_an_impossible_port_is_still_refused()
    {
        ConfigurationDocument document = Valid();
        document.Dashboard = new DashboardDocument { Enabled = false, Port = 70_000 };

        ConfigurationValidation result = Validate(document);

        Assert.Contains(result.Errors, error => error.Path == "dashboard.port");
    }

    private static ConfigurationDocument Valid() => ValidDocument.Create();

    private static ConfigurationValidation Validate(ConfigurationDocument document) =>
        ConfigurationValidator.Validate(document, ValidDocument.MachineName);
}
