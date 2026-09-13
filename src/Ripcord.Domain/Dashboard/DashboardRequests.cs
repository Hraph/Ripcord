namespace Ripcord.Domain.Dashboard;

/// What the server does with one request. Three answers, because there are three: serve the
/// page, refuse the verb, or say there is nothing at that path.
public enum DashboardVerdictOnRequest
{
    Serve,
    MethodNotAllowed,
    NotFound,
}

/// The entire surface the page is served on. It lives here rather than in the adapter because
/// "this server answers a read of one path and nothing else" is the security property the
/// milestone rests on, and a property that cannot be tested without Windows is a property
/// nobody checks.
public static class DashboardRequests
{
    /// Exact and case-sensitive, which is what the HTTP specification says method names are.
    /// Being lenient here would mean the set of verbs this server accepts is larger than the
    /// set anybody reviewed.
    public static DashboardVerdictOnRequest Judge(string? method, string? path) =>
        method switch
        {
            "GET" or "HEAD" => path == "/"
                ? DashboardVerdictOnRequest.Serve
                : DashboardVerdictOnRequest.NotFound,
            _ => DashboardVerdictOnRequest.MethodNotAllowed,
        };
}
