using Ripcord.Domain.Dashboard;

namespace Ripcord.Tests.Dashboard;

/// The whole of what the server will answer. It is a decision rather than plumbing: a
/// read-only page that answered a POST, or served a second path, would no longer be the thing
/// that was reviewed — so it is settled here, where it can be read and tested without a host.
public sealed class DashboardRequestTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public void The_page_is_served_to_a_read(string method)
    {
        Assert.Equal(DashboardVerdictOnRequest.Serve, DashboardRequests.Judge(method, "/"));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("OPTIONS")]
    [InlineData("TRACE")]
    [InlineData("get")]
    public void Anything_that_is_not_a_read_is_refused(string method)
    {
        Assert.Equal(
            DashboardVerdictOnRequest.MethodNotAllowed, DashboardRequests.Judge(method, "/"));
    }

    /// One page, one path. There is nothing else to serve, and a server that looked for a
    /// second one would be a server that can be asked for a file.
    [Theory]
    [InlineData("/index.html")]
    [InlineData("/favicon.ico")]
    [InlineData("/../ripcord.yaml")]
    [InlineData("//")]
    [InlineData("")]
    public void Every_path_but_the_root_is_absent(string path)
    {
        Assert.Equal(DashboardVerdictOnRequest.NotFound, DashboardRequests.Judge("GET", path));
    }

    /// The method is judged first: an operator pointing a tool at the wrong verb should be
    /// told the verb is wrong, not that the page does not exist.
    [Fact]
    public void A_write_to_a_path_that_does_not_exist_is_still_refused_as_a_write()
    {
        Assert.Equal(
            DashboardVerdictOnRequest.MethodNotAllowed,
            DashboardRequests.Judge("POST", "/index.html"));
    }
}
