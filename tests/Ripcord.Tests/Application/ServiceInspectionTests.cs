using Ripcord.Application.Deployment;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Diagnostics;
using Ripcord.Ports.Configuration;
using Ripcord.Ports.Deployment;
using Ripcord.Ports.Diagnostics;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Application;

/// `ripcord service` reports the service whatever else fails to read.
public class ServiceInspectionTests
{
    private const string ThisBinary = @"C:\Ripcord\ripcord.exe";

    private const string ServiceBinary = @"C:\Program Files\Ripcord\ripcord.exe";

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 0, 30, 0, TimeSpan.Zero);

    private static readonly ObservedService StoppedService = new(
        true, $"\"{ServiceBinary}\" serve", ServiceRunState.Stopped, "Auto", 0, 0);

    private static DeploymentRequest Request() =>
        new(@"C:\Ripcord\ripcord.yaml", ValidDocument.MachineName, ThisBinary, Remove: false);

    private static MemoryConfigStore Valid() =>
        new(ConfigurationRead.Succeeded(ValidDocument.Create()));

    [Fact]
    public void Invalid_configuration_still_reports_the_service()
    {
        ServiceReport report = new ServiceInspection(
                new MemoryConfigStore(ConfigurationRead.Failed("node", "missing")),
                new Host(StoppedService),
                new Logs())
            .Inspect(Request(), Now);

        Assert.Equal(StoppedService, report.Service);
        Assert.Null(report.Deployment.Observed);
        Assert.NotEmpty(report.Deployment.Errors);
        Assert.StartsWith("no log today or yesterday", report.Verdict.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_windows_is_a_line_not_a_crash()
    {
        ServiceReport report = new ServiceInspection(Valid(), new Host(null), new Logs())
            .Inspect(Request(), Now);

        Assert.Equal(ServiceRunState.Unknown, report.Service.State);
        Assert.Equal("Windows did not say whether it runs: WMI refused", report.Verdict.Why);
    }

    /// The service writes beside the binary Windows runs, not beside the one reading.
    [Fact]
    public void Logs_folder_is_the_services_and_yesterday_is_read_when_today_is_absent()
    {
        Logs logs = new(@"C:\Program Files\Ripcord\logs\listener-2026-09-24.log");

        ServiceReport report = new ServiceInspection(Valid(), new Host(StoppedService), logs)
            .Inspect(Request(), Now);

        Assert.Equal(@"C:\Program Files\Ripcord\logs", report.LogsFolder);
        Assert.Equal(
            [
                @"C:\Program Files\Ripcord\logs\listener-2026-09-25.log",
                @"C:\Program Files\Ripcord\logs\listener-2026-09-24.log",
            ],
            logs.Asked);
        Assert.Equal(@"C:\Program Files\Ripcord\logs\listener-2026-09-24.log", report.Log?.Path);
    }

    /// The configuration's logs access is about this binary's folder: it says nothing about a
    /// service that runs from another one.
    [Theory]
    [InlineData(ThisBinary, true)]
    [InlineData(ServiceBinary, false)]
    public void Logs_access_is_blamed_only_for_the_folder_it_was_read_on(
        string serviceBinary, bool blamed)
    {
        ServiceReport report = new ServiceInspection(
                Valid(),
                new Host(StoppedService with { CommandLine = $"\"{serviceBinary}\" serve" }),
                new Logs())
            .Inspect(Request(), Now);

        Assert.Equal(
            blamed,
            report.Verdict.Why?.Contains("cannot write its logs folder", StringComparison.Ordinal));
    }

    /// One reading per report: two could disagree on one screen, and each costs a CIM query.
    [Fact]
    public void The_service_is_read_once_and_that_reading_is_what_the_plan_sees()
    {
        Host host = new(StoppedService);

        new ServiceInspection(Valid(), host, new Logs()).Inspect(Request(), Now);

        Assert.Equal(1, host.Readings);
        Assert.Equal([StoppedService], host.Given);
    }

    [Theory]
    [InlineData(null, SnapshotAge.Missing)]
    [InlineData(60, SnapshotAge.Fresh)]
    [InlineData(121, SnapshotAge.Stale)]
    public void The_snapshot_is_judged_against_the_peer_offline_threshold(
        int? secondsAgo, SnapshotAge expected)
    {
        // The sample's peer.offline_after_sec is 120.
        Host host = new(StoppedService)
        {
            WrittenAt = secondsAgo is { } seconds ? Now.AddSeconds(-seconds) : null,
        };

        ServiceReport report = new ServiceInspection(Valid(), host, new Logs()).Inspect(Request(), Now);

        Assert.Equal(expected, report.Snapshot);
    }

    /// Logs not writable by the service; ObserveService throws when given nothing.
    private sealed class Host(ObservedService? service) : IDeploymentExecutor
    {
        public int Readings { get; private set; }

        public List<ObservedService> Given { get; } = [];

        public DateTimeOffset? WrittenAt { get; init; }

        public ObservedDeployment Observe(DesiredDeployment desired, ObservedService service)
        {
            this.Given.Add(service);
            return ObservedDeployment.Nothing with
            {
                ServiceInstalled = true,
                SnapshotWrittenAt = this.WrittenAt,
            };
        }

        public ObservedService ObserveService()
        {
            this.Readings++;
            return service ?? throw new InvalidOperationException("WMI refused");
        }

        public void Apply(DeploymentStep change, DesiredDeployment desired) =>
            throw new InvalidOperationException("inspection must not change anything");
    }

    private sealed class Logs(string? present = null) : IDiagnosticLogReader
    {
        public List<string> Asked { get; } = [];

        public LogReading? Tail(string path, int maxLines)
        {
            this.Asked.Add(path);
            return path == present ? new LogReading(path, [], null) : null;
        }
    }
}
