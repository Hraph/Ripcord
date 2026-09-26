using Ripcord.Application.Deployment;
using Ripcord.Domain.Deployment;
using Ripcord.Domain.Diagnostics;
using Ripcord.Ports.Deployment;
using Ripcord.Ports.Diagnostics;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Application;

/// After `ripcord update` the binary on disk is new and both services still run the old one:
/// `service install` restarts them, from what each recorded when it started.
public class ListenerDeploymentTests
{
    private const string Binary = @"C:\Ripcord\ripcord.exe";

    private const string New = "0.8.0+new0000";

    private static readonly ObservedService Running = new(
        true, $"\"{Binary}\" serve", ServiceRunState.Running, "Auto", 0, 0, ProcessId: 4812);

    private static readonly ObservedService Publishing = new(
        true,
        $"\"{Binary}\" publish",
        ServiceRunState.Running,
        "Auto",
        0,
        0,
        ProcessId: 4900,
        DisplayName: RipcordService.Publisher.DisplayName,
        Description: RipcordService.Publisher.Description);

    [Theory]
    [InlineData("0.7.0+old0000", true)]
    [InlineData(New, false)]
    public void A_service_on_the_old_build_is_restarted_by_install(string recorded, bool restarted)
    {
        Files records = new()
        {
            [@"C:\Ripcord\logs\listener\listener-process.txt"] = [recorded, "4812"],
            [@"C:\Ripcord\logs\publish\publisher-process.txt"] = [recorded, "4900"],
        };

        DeploymentOutcome outcome = new ListenerDeployment(
                new MemoryConfigStore(Ripcord.Ports.Configuration.ConfigurationRead.Succeeded(ValidDocument.Create())),
                new Deployed(),
                new DeploymentBuild(records, New))
            .Plan(new DeploymentRequest(@"C:\Ripcord\ripcord.yaml", ValidDocument.MachineName, Binary, false));

        Assert.Equal(restarted, outcome.Observed!.ListenerOutdated);
        Assert.Equal(restarted, outcome.Observed.Publisher.Outdated);
        Assert.Equal(
            restarted ? 2 : 0,
            outcome.Plan!.Steps.Count(step => step.Action == DeploymentAction.RestartService));
    }

    /// A 0.9.0 listener recorded its build in `logs` itself: still read, or the update that
    /// moves it to `logs\listener` would leave it running and writing where it is losing access.
    [Fact]
    public void A_listener_recorded_in_the_old_logs_folder_is_restarted_by_install()
    {
        Files records = new()
        {
            [@"C:\Ripcord\logs\listener-process.txt"] = ["0.9.0+old0000", "4812"],
        };

        DeploymentOutcome outcome = new ListenerDeployment(
                new MemoryConfigStore(Ripcord.Ports.Configuration.ConfigurationRead.Succeeded(ValidDocument.Create())),
                new Deployed(),
                new DeploymentBuild(records, New))
            .Plan(new DeploymentRequest(@"C:\Ripcord\ripcord.yaml", ValidDocument.MachineName, Binary, false));

        Assert.True(outcome.Observed!.ListenerOutdated);
    }

    /// Without the build to compare against, nothing is looked for: removal and inspection.
    [Fact]
    public void Without_a_build_no_record_is_read()
    {
        Files records = new();

        DeploymentOutcome outcome = new ListenerDeployment(
                new MemoryConfigStore(Ripcord.Ports.Configuration.ConfigurationRead.Succeeded(ValidDocument.Create())),
                new Deployed())
            .Plan(new DeploymentRequest(@"C:\Ripcord\ripcord.yaml", ValidDocument.MachineName, Binary, false));

        Assert.False(outcome.Observed!.ListenerOutdated);
        Assert.Empty(records.Asked);
    }

    private sealed class Deployed : IDeploymentExecutor
    {
        public ObservedDeployment Observe(DesiredDeployment desired, ObservedService service) =>
            new(
                ServiceInstalled: true,
                ServiceBinaryPath: Binary,
                FirewallRuleInstalled: true,
                FirewallPort: desired.Port,
                FirewallRemoteAddress: desired.PeerAddress,
                SnapshotReadableByService: true,
                ServiceRunning: true,
                LogsWritableByService: true,
                EventSourceRegistered: true,
                KeyReadableByService: true,
                ConfigurationReadableByService: true,
                ListenerRecovers: true,
                ListenerDescribed: true)
            {
                Publisher = Deployment.DeploymentPlanTests.PublisherInPlace(Binary, NamespaceGrant.Granted)
                    with { Service = Publishing },
            };

        public ObservedService ObserveService(RipcordService which) =>
            which == RipcordService.Publisher ? Publishing : Running;

        public void Apply(DeploymentStep change, DesiredDeployment desired) =>
            throw new InvalidOperationException("planning must not change anything");
    }

    private sealed class Files : Dictionary<string, string[]>, IDiagnosticLogReader
    {
        public List<string> Asked { get; } = [];

        public LogReading? Tail(string path, int maxLines)
        {
            this.Asked.Add(path);
            return this.TryGetValue(path, out string[]? lines) ? new LogReading(path, lines, null) : null;
        }
    }
}
