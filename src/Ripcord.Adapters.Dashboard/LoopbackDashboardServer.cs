using System.Globalization;
using System.Net;
using System.Text;
using Ripcord.Domain.Dashboard;
using Ripcord.Ports.Dashboard;

namespace Ripcord.Adapters.Dashboard;

/// `HttpListener` bound to `127.0.0.1` and nothing else. The address is not configurable and
/// not computed: it is written here, once, so the page cannot be widened onto the network by
/// editing a file.
///
/// The framework's own listener rather than a web stack, because the whole surface is one
/// document served to one interface. Every decision it makes — which verb, which path — comes
/// from `DashboardRequests` in the Domain; this class translates and does the I/O.
public sealed class LoopbackDashboardServer(Action<string>? report = null) : IDashboardServer
{
    private const string Loopback = "127.0.0.1";

    public async Task RunAsync(
        DashboardSettings settings,
        Func<CancellationToken, Task<string>> page,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(page);

        using HttpListener listener = new();

        listener.Prefixes.Add(string.Create(
            CultureInfo.InvariantCulture, $"http://{Loopback}:{settings.Port}/"));

        listener.Start();

        // Stop unblocks the pending GetContextAsync; without this the loop outlives Ctrl+C.
        using CancellationTokenRegistration stop =
            cancellationToken.Register(listener.Stop);

        try
        {
            // One request at a time. The readership is one operator on one console, and a
            // page that answers in sequence is a page with no concurrency to get wrong.
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context =
                    await listener.GetContextAsync().ConfigureAwait(false);

                await this.AnswerAsync(context, page, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
            when (exception is HttpListenerException or ObjectDisposedException
                && cancellationToken.IsCancellationRequested)
        {
            // Stop() during a pending accept. Cancellation, not a failure.
        }
    }

    /// One request can never end the loop. A page that failed to build is already a page
    /// saying so, and anything thrown past that is reported and answered with a 500 — a
    /// server that died on one malformed request would be down for the rest of the incident.
    private async Task AnswerAsync(
        HttpListenerContext context,
        Func<CancellationToken, Task<string>> page,
        CancellationToken cancellationToken)
    {
        try
        {
            DashboardVerdictOnRequest verdict = DashboardRequests.Judge(
                context.Request.HttpMethod, context.Request.Url?.AbsolutePath);

            switch (verdict)
            {
                case DashboardVerdictOnRequest.Serve:
                    await Write(
                            context.Response,
                            HttpStatusCode.OK,
                            await page(cancellationToken).ConfigureAwait(false),
                            context.Request.HttpMethod == "HEAD")
                        .ConfigureAwait(false);
                    break;

                case DashboardVerdictOnRequest.MethodNotAllowed:
                    context.Response.Headers.Add("Allow", "GET, HEAD");
                    await Write(
                            context.Response,
                            HttpStatusCode.MethodNotAllowed,
                            "This page is read-only.")
                        .ConfigureAwait(false);
                    break;

                default:
                    await Write(context.Response, HttpStatusCode.NotFound, "No such page.")
                        .ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            report?.Invoke($"ripcord: the dashboard could not answer a request: {exception.Message}");

            try
            {
                await Write(
                        context.Response,
                        HttpStatusCode.InternalServerError,
                        "The page could not be built.")
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Nothing left to do: the client is gone, or the response has already begun
                // and its status can no longer be set. Either way the loop goes on — a
                // browser tab closed mid-load must not end the server.
            }
        }
    }

    /// The headers a page that fetches nothing and executes nothing should carry. Stated here
    /// rather than left to the framework's defaults: a cached dashboard would show yesterday's
    /// reading, which is the one thing this page must never do.
    private static async Task Write(
        HttpListenerResponse response,
        HttpStatusCode status,
        string body,
        bool headersOnly = false)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);

        response.StatusCode = (int)status;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.Headers.Add("Cache-Control", "no-store");
        response.Headers.Add("Content-Security-Policy", "default-src 'none'; style-src 'unsafe-inline'");
        response.Headers.Add("Referrer-Policy", "no-referrer");
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        response.Headers.Add("X-Frame-Options", "DENY");

        if (!headersOnly)
        {
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }

        response.Close();
    }
}
