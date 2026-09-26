using System.Net;
using System.Net.Sockets;
using System.Text;
using Ripcord.Adapters.Update;
using Ripcord.Domain.Updates;
using Ripcord.Ports.Updates;

namespace Ripcord.Tests.Adapters;

/// The download, against a real socket. It fetches something this host is about to run, so
/// what matters here is not the happy path but the refusals: a body that will not fit in
/// memory, a release with no signature beside it, and a server that says nothing at all.
public sealed class HttpReleaseSourceTests
{
    private static readonly byte[] Binary = Encoding.UTF8.GetBytes("a released ripcord.exe");

    private static readonly byte[] Signature = Encoding.UTF8.GetBytes("a detached signature");

    private const long RepositoryId = 1_367_653_231;

    private const long BinaryAssetId = 900_001;

    private const long SignatureAssetId = 900_002;

    [Fact]
    public async Task A_published_release_arrives_with_its_signature()
    {
        await using Origin origin = await Origin.StartAsync();

        FetchedRelease release = await origin.Source().FetchAsync("0.2.0", null, default);

        Assert.True(release.Arrived);
        Assert.Equal(Binary, release.Payload);
        Assert.Equal(Signature, release.Signature);
    }

    [Fact]
    public async Task The_binary_is_reported_as_it_arrives()
    {
        await using Origin origin = await Origin.StartAsync();
        List<DownloadedBytes> heard = [];

        await origin.Source().FetchAsync("0.2.0", heard.Add, default);

        Assert.NotEmpty(heard);
        Assert.Equal(Binary.Length, heard[^1].Received);
    }

    /// A release published without its signature cannot be verified, so it cannot be
    /// installed. Said by name rather than left to look like a network failure.
    [Fact]
    public async Task A_release_with_no_signature_beside_it_is_refused_by_name()
    {
        await using Origin origin = await Origin.StartAsync(publishSignature: false);

        FetchedRelease release = await origin.Source().FetchAsync("0.2.0", null, default);

        Assert.False(release.Arrived);
        // Names the file, not the concept: "signature" sends somebody to read about
        // signing, "ripcord.exe.sig" sends them to look at the release page.
        Assert.Contains(
            "ripcord.exe.sig", release.FailureMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_version_that_was_never_published_is_a_failure_not_an_exception()
    {
        await using Origin origin = await Origin.StartAsync(publishBinary: false);

        FetchedRelease release = await origin.Source().FetchAsync("0.2.0", null, default);

        Assert.False(release.Arrived);
        Assert.NotNull(release.FailureMessage);
    }

    /// A host about to perform a failover must not have its memory filled by a release. The
    /// cap is checked against the declared length and again while reading, because a response
    /// that lies about its length is exactly the one worth capping.
    [Fact]
    public async Task A_body_over_the_cap_is_refused_rather_than_read()
    {
        await using Origin origin = await Origin.StartAsync();

        FetchedRelease release = await origin.Source(maximumBytes: 4).FetchAsync("0.2.0", null, default);

        Assert.False(release.Arrived);
    }

    /// A chunked response declares no length at all, so the header check has nothing to
    /// refuse and the cap has to hold while reading. That is the case worth testing: the
    /// declared length is a hint from the same server that is sending the body.
    [Fact]
    public async Task A_body_that_declares_no_length_is_still_capped()
    {
        await using Origin origin = await Origin.StartAsync(chunked: true);

        FetchedRelease release = await origin.Source(maximumBytes: 4).FetchAsync("0.2.0", null, default);

        Assert.False(release.Arrived);
    }

    /// The tag comes off the release feed. It reaches the path escaped, so a tag that is not
    /// a version number cannot walk out of the release it names.
    [Fact]
    public async Task A_tag_cannot_escape_its_own_path()
    {
        await using Origin origin = await Origin.StartAsync();

        await origin.Source().FetchAsync("../../../etc/passwd", null, default);

        Assert.All(origin.Requested, path =>
            Assert.DoesNotContain("etc/passwd", path, StringComparison.Ordinal));
    }

    /// `github.com/repositories/{id}` is not a route — the numeric id is an API path and never
    /// a web one — so the URL this adapter used to build returned 404 for every release on
    /// every host. Nothing may go back to building one.
    [Fact]
    public async Task Every_request_is_addressed_by_id_and_names_no_repository()
    {
        await using Origin origin = await Origin.StartAsync();

        await origin.Source().FetchAsync("0.2.0", null, default);

        Assert.NotEmpty(origin.Requested);

        Assert.All(origin.Requested, path =>
        {
            Assert.StartsWith($"/repositories/{RepositoryId}/", path, StringComparison.Ordinal);
            Assert.DoesNotContain("Ripcord", path, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// Only the asset ids are read out of the metadata. An address in a response body is
    /// somebody else's text, and following one would be the whole of a supply chain attack.
    [Fact]
    public async Task A_download_address_offered_by_the_metadata_is_not_followed()
    {
        await using Origin origin = await Origin.StartAsync(offerAnotherAddress: true);

        FetchedRelease release = await origin.Source().FetchAsync("0.2.0", null, default);

        Assert.True(release.Arrived);
        Assert.All(origin.Requested, path =>
            Assert.DoesNotContain("elsewhere", path, StringComparison.OrdinalIgnoreCase));
    }

    /// A host with no outbound access is the normal case for this pair: it has to report that
    /// it could not look, never that it is current.
    [Fact]
    public async Task A_server_that_never_answers_fails_rather_than_hangs()
    {
        await using Origin origin = await Origin.StartAsync(hang: true);

        FetchedRelease release = await origin
            .Source(timeout: TimeSpan.FromMilliseconds(300))
            .FetchAsync("0.2.0", null, default);

        Assert.False(release.Arrived);
        Assert.NotNull(release.FailureMessage);
    }

    /// A release origin on the loopback interface, standing in for github.com.
    private sealed class Origin : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly HttpListener listener = new();
        private readonly List<string> requested = [];
        private Task? loop;

        private int Port { get; set; }

        public IReadOnlyList<string> Requested => this.requested;

        public static async Task<Origin> StartAsync(
            bool publishBinary = true,
            bool publishSignature = true,
            bool chunked = false,
            bool hang = false,
            bool offerAnotherAddress = false)
        {
            Origin origin = new() { Port = FreePort() };

            origin.listener.Prefixes.Add($"http://127.0.0.1:{origin.Port}/");
            origin.listener.Start();

            origin.loop = Task.Run(() =>
                origin.ServeAsync(
                    publishBinary, publishSignature, chunked, hang, offerAnotherAddress));

            await Task.Yield();
            return origin;
        }

        public HttpReleaseSource Source(
            long maximumBytes = 1024, TimeSpan? timeout = null) =>
            new(
                RepositoryId,
                "ripcord/test",
                timeout ?? TimeSpan.FromSeconds(10),
                maximumBytes,
                new ReleaseOrigin(new Uri($"http://127.0.0.1:{this.Port}/")));

        public async ValueTask DisposeAsync()
        {
            await this.cancellation.CancelAsync();
            this.listener.Stop();

            if (this.loop is { } running)
            {
                try
                {
                    await running;
                }
                catch (Exception exception) when (
                    exception is HttpListenerException
                        or ObjectDisposedException
                        or OperationCanceledException)
                {
                    // Stopped while accepting, or while deliberately hanging.
                }
            }

            this.listener.Close();
            this.cancellation.Dispose();
        }

        private async Task ServeAsync(
            bool publishBinary,
            bool publishSignature,
            bool chunked,
            bool hang,
            bool offerAnotherAddress = false)
        {
            while (!this.cancellation.IsCancellationRequested)
            {
                HttpListenerContext context = await this.listener.GetContextAsync();
                string path = context.Request.Url!.AbsolutePath;

                lock (this.requested)
                {
                    this.requested.Add(path);
                }

                if (hang)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), this.cancellation.Token);
                }

                // The release metadata, in the shape the API answers with: the assets carry
                // names and ids, and nothing here hands back a download address.
                if (path.EndsWith("/releases/tags/0.2.0", StringComparison.Ordinal))
                {
                    List<string> entries = [];

                    if (publishBinary)
                    {
                        entries.Add(offerAnotherAddress
                            ? $$"""{"name":"ripcord.exe","id":{{BinaryAssetId}},"browser_download_url":"http://elsewhere.invalid/ripcord.exe"}"""
                            : $$"""{"name":"ripcord.exe","id":{{BinaryAssetId}}}""");
                    }

                    if (publishSignature)
                    {
                        entries.Add($$"""{"name":"ripcord.exe.sig","id":{{SignatureAssetId}}}""");
                    }

                    byte[] metadata = Encoding.UTF8.GetBytes(
                        $$"""{"tag_name":"0.2.0","assets":[{{string.Join(",", entries)}}]}""");

                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = metadata.Length;
                    await context.Response.OutputStream.WriteAsync(metadata, this.cancellation.Token);
                    context.Response.Close();
                    continue;
                }

                if (!path.EndsWith($"/releases/assets/{BinaryAssetId}", StringComparison.Ordinal)
                    && !path.EndsWith($"/releases/assets/{SignatureAssetId}", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 404;
                    context.Response.Close();
                    continue;
                }

                bool signature = path.EndsWith(
                    $"/releases/assets/{SignatureAssetId}", StringComparison.Ordinal);

                byte[] body = signature ? Signature : Binary;

                // Chunked declares no length, so the cap has to hold while reading rather
                // than against the header.
                if (chunked)
                {
                    context.Response.SendChunked = true;
                }
                else
                {
                    context.Response.ContentLength64 = body.Length;
                }
                await context.Response.OutputStream.WriteAsync(body, this.cancellation.Token);
                context.Response.Close();
            }
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
