using Ripcord.Domain.Deployment;
using Ripcord.Domain.Diagnostics;

namespace Ripcord.Tests.Deployment;

/// The two services one binary runs, and what each one's name decides.
public class RipcordServiceTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("serve", "ripcord", @"NT SERVICE\ripcord")]
    [InlineData("publish", "ripcord-publish", @"NT SERVICE\ripcord-publish")]
    public void A_verb_the_service_manager_starts_names_its_service_and_account(
        string verb, string name, string account)
    {
        RipcordService service = RipcordService.ForVerb(verb)!;

        Assert.Equal(name, service.Name);
        Assert.Equal(account, service.Account);
        Assert.Equal($"\"C:\\Ripcord\\ripcord.exe\" {verb}", service.CommandLine(@"C:\Ripcord\ripcord.exe"));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("Serve")]
    [InlineData(null)]
    public void Any_other_verb_is_no_service(string? verb) =>
        Assert.Null(RipcordService.ForVerb(verb));

    /// Each writes its own file, and each is pruned like the others.
    [Fact]
    public void The_publisher_logs_under_its_own_prefix_and_is_pruned()
    {
        Assert.Equal("publish-2026-09-26.log", LogFolder.FileName(DiagnosticOrigin.Publisher, At));
        Assert.Equal(
            ["publish-2026-08-01.log"],
            LogFolder.Expired(["publish-2026-08-01.log", "publish-2026-09-26.log"], At));
    }

    /// The banner names the role, and `ripcord service` still finds either kind.
    [Fact]
    public void Each_banner_names_its_role_and_both_are_recognised()
    {
        DiagnosticEntry publisher = ServiceStartup.Banner("0.8.0+abc", "ripcord.yaml", RipcordService.Publisher);
        DiagnosticEntry listener = ServiceStartup.Banner("0.8.0+abc", "ripcord.yaml");

        Assert.EndsWith("publisher starting", publisher.Message, StringComparison.Ordinal);
        Assert.EndsWith("listener starting", listener.Message, StringComparison.Ordinal);
        Assert.True(ServiceStartup.IsBanner(publisher.Operation, publisher.Message));
        Assert.True(ServiceStartup.IsBanner(listener.Operation, "==== ripcord 0.4.1 listener starting"));
    }

    [Fact]
    public void A_publisher_that_cannot_log_names_its_own_account()
    {
        string text = ServiceStartup.LogUnavailable(
            @"C:\Program Files\Ripcord\logs\publish\publish-2026-09-26.log", "Access is denied.", RipcordService.Publisher);

        Assert.Contains("The Ripcord publisher stopped", text, StringComparison.Ordinal);
        Assert.Contains(@"NT SERVICE\ripcord-publish", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_publishers_log_candidates_are_its_own_files() =>
        Assert.Equal(
            [@"C:\R\logs\publish\publish-2026-09-26.log", @"C:\R\logs\publish\publish-2026-09-25.log"],
            ServiceLog.Candidates(@"C:\R\logs\publish", At, RipcordService.Publisher));
}
