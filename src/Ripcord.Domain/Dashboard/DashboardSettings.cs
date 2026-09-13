namespace Ripcord.Domain.Dashboard;

/// The read-only page. Disabled is the default and the ordinary state: a disaster recovery
/// tool that opens a listening socket nobody asked for has widened the attack surface of a
/// Hyper-V host to save a command.
///
/// There is no address: the page is bound to the loopback interface by construction. A key
/// for it would be a key somebody can widen, and the one thing this page must never be is
/// reachable from the network.
public sealed record DashboardSettings(bool Enabled, int Port, TimeSpan Refresh)
{
    public const int DefaultPort = 7080;

    /// The shortest interval the page is allowed to reload at. Every refresh re-reads
    /// Hyper-V, and a page reloading every second turns a host already in trouble into a host
    /// answering WMI queries in a loop.
    public static readonly TimeSpan MinimumRefresh = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan MaximumRefresh = TimeSpan.FromDays(1);

    /// The replication frequency these hosts run at. A page that refreshes faster than the
    /// state it displays can change is only reporting the same reading twice.
    public static readonly TimeSpan DefaultRefresh = TimeSpan.FromSeconds(30);

    public static DashboardSettings Disabled() => new(false, DefaultPort, DefaultRefresh);
}
