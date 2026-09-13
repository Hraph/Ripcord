using Ripcord.Domain.Dashboard;

namespace Ripcord.Ports.Dashboard;

/// Serves one read-only page on the loopback interface, and nothing else. Runs until
/// cancelled.
///
/// The page is a callback rather than a value: it is rebuilt for each request, so a host that
/// became unreadable between two refreshes says so on the page instead of showing the last
/// reading that worked.
public interface IDashboardServer
{
    Task RunAsync(
        DashboardSettings settings,
        Func<CancellationToken, Task<string>> page,
        CancellationToken cancellationToken);
}
