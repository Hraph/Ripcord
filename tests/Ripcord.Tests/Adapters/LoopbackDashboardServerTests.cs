using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Ripcord.Adapters.Dashboard;
using Ripcord.Domain.Dashboard;

namespace Ripcord.Tests.Adapters;

/// The adapter against a real socket. It decides nothing — every verdict comes from
/// `DashboardRequests` — but "bound to the loopback interface and nowhere else" is a claim
/// about a socket, and a claim about a socket is only worth what a socket says.
public sealed class LoopbackDashboardServerTests
{
    private const string Page = "<!DOCTYPE html><html><body>ripcord</body></html>";

    [Fact]
    public async Task A_read_of_the_root_is_answered_with_the_page()
    {
        await using Served served = await Served.StartAsync();

        HttpResponseMessage response = await served.Client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Page, await response.Content.ReadAsStringAsync());
    }

    /// The page is rebuilt for each request, never cached and never reused: a dashboard
    /// showing the previous reading is the failure this milestone exists to avoid.
    [Fact]
    public async Task Each_request_rebuilds_the_page()
    {
        await using Served served = await Served.StartAsync();

        await served.Client.GetAsync(new Uri("/", UriKind.Relative));
        await served.Client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(2, served.Builds);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Anything_that_is_not_a_read_is_refused(string method)
    {
        await using Served served = await Served.StartAsync();

        HttpResponseMessage response = await served.Client.SendAsync(
            new HttpRequestMessage(new HttpMethod(method), new Uri("/", UriKind.Relative)));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(0, served.Builds);
    }

    [Fact]
    public async Task Any_other_path_is_absent()
    {
        await using Served served = await Served.StartAsync();

        HttpResponseMessage response =
            await served.Client.GetAsync(new Uri("/ripcord.yaml", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, served.Builds);
    }

    [Fact]
    public async Task The_page_is_served_uncacheable_and_unexecutable()
    {
        await using Served served = await Served.StartAsync();

        HttpResponseMessage response = await served.Client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal("no-store", string.Join("", response.Headers.CacheControl!.ToString()));
        Assert.Contains("default-src 'none'", Header(response, "Content-Security-Policy"), StringComparison.Ordinal);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
    }

    /// One bad request must not end the loop. A server that died on the first failure would
    /// be down for the rest of the incident it was opened for.
    [Fact]
    public async Task A_page_that_cannot_be_built_is_answered_and_the_server_keeps_serving()
    {
        await using Served served = await Served.StartAsync(
            build: count => count == 1
                ? throw new InvalidOperationException("WMI refused the query")
                : Page);

        HttpResponseMessage first = await served.Client.GetAsync(new Uri("/", UriKind.Relative));
        HttpResponseMessage second = await served.Client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains(
            served.Reported, line => line.Contains("WMI refused the query", StringComparison.Ordinal));
    }

    /// A browser tab closed while the page is loading is an ordinary event, not a failure.
    /// Writing to the socket fails after the response has begun, at which point its status
    /// can no longer be set either — and the server has to go on serving regardless.
    [Fact]
    public async Task A_client_that_vanishes_mid_response_does_not_end_the_server()
    {
        await using Served served = await Served.StartAsync(
            build: _ => new string('x', 4 * 1024 * 1024));

        using (TcpClient abandoning = new())
        {
            await abandoning.ConnectAsync(IPAddress.Loopback, served.Port);

            await abandoning.GetStream().WriteAsync(
                Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));

            abandoning.Client.Close();
        }

        HttpResponseMessage next = await served.Client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    /// A HEAD is a read, so it is answered — with the headers of the page and none of it.
    [Fact]
    public async Task A_head_request_is_answered_with_the_headers_and_no_body()
    {
        await using Served served = await Served.StartAsync();

        HttpResponseMessage response = await served.Client.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, new Uri("/", UriKind.Relative)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Page.Length, response.Content.Headers.ContentLength);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, served.Builds);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values)
            ? string.Join(",", values)
            : "";

    /// A server on a port the operating system handed out, torn down with the test. Nothing
    /// here may depend on a fixed port: the suite runs in a container beside other tests.
    private sealed class Served : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly List<string> reported = [];
        private Task? loop;
        private int builds;

        public HttpClient Client { get; private set; } = null!;

        public int Port { get; private set; }

        public int Builds => this.builds;

        public IReadOnlyList<string> Reported => this.reported;

        public static async Task<Served> StartAsync(Func<int, string>? build = null)
        {
            Served served = new();
            int port = FreePort();
            served.Port = port;

            LoopbackDashboardServer server = new(served.reported.Add);

            served.loop = server.RunAsync(
                new DashboardSettings(true, port, TimeSpan.FromSeconds(30)),
                _ => Task.FromResult(
                    (build ?? (_ => Page))(Interlocked.Increment(ref served.builds))),
                served.cancellation.Token);

            served.Client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
                Timeout = TimeSpan.FromSeconds(10),
            };

            await WaitUntilListening(served.Client);
            return served;
        }

        public async ValueTask DisposeAsync()
        {
            await this.cancellation.CancelAsync();

            if (this.loop is { } running)
            {
                await running;
            }

            this.Client.Dispose();
            this.cancellation.Dispose();
        }

        private static async Task WaitUntilListening(HttpClient client)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    // A path the server has nothing at: it answers 404 without building a
                    // page, so waiting for the socket never costs a reading.
                    await client.GetAsync(new Uri("/waiting", UriKind.Relative));
                    return;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(20);
                }
            }

            throw new InvalidOperationException("the dashboard never started listening");
        }

        private static int FreePort()
        {
            using TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            return port;
        }
    }
}
