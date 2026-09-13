using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ripcord.Adapters.Pairing.Wire;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Pairing;
using Ripcord.Ports;
using Ripcord.Ports.Pairing;

namespace Ripcord.Adapters.Pairing.Transport;

/// What a connection to the listener was allowed to do. Returned rather than logged here so
/// the caller owns the log, and so the tests can assert refusals precisely.
public sealed record ServedConnection(PeerVerdict Verdict, string RemoteAddress, bool Served);

/// The server half. It accepts one connection at a time, checks who called, writes the
/// snapshot and hangs up. It reads nothing from the network — the protocol has no request —
/// so the parser risk the threat model is about lives entirely on the client side.
///
/// It never touches Hyper-V (decision D18): everything it serves came from a file the
/// privileged `ripcord` wrote.
public sealed class SnapshotListener(
    Func<X509Certificate2> localCertificate,
    PeerTrust trust,
    ISnapshotStore snapshotStore,
    IClock clock,
    TimeSpan? connectionDeadline = null) : IAsyncDisposable
{
    /// How long one caller may hold the listener once it has connected. The design serves one
    /// at a time, so a caller that connects and then stalls would otherwise deny the pair view
    /// permanently.
    ///
    /// It starts when the connection arrives, not when the wait for one does: a deadline that
    /// covered the idle wait would leave a connection arriving late in the window a fraction
    /// of a second to complete a handshake, and refuse the real peer for arriving at the
    /// wrong moment.
    public static readonly TimeSpan DefaultConnectionDeadline = TimeSpan.FromSeconds(15);

    private TcpListener? listener;

    /// The port the listener actually bound, which is the requested one unless the caller
    /// asked for 0 — the tests do, so they never collide with a real service.
    public int Port => ((IPEndPoint)this.listener!.LocalEndpoint).Port;

    public void Start(IPAddress address, int port)
    {
        this.listener = new TcpListener(address, port);
        this.listener.Start();
    }

    /// One connection at a time, on purpose: there are exactly two hosts, and a queue is one
    /// more thing an attacker can fill.
    public async Task<ServedConnection> ServeOneAsync(
        string snapshotPath, PeerRules rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);

        using TcpClient client = await this.listener!
            .AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);

        using CancellationTokenSource connection =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        connection.CancelAfter(connectionDeadline ?? DefaultConnectionDeadline);

        CancellationToken deadline = connection.Token;

        string remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";

        // Fail closed: if the validation callback never runs, nothing is served.
        PeerVerdict observed = PeerVerdict.NoCertificate;

        using X509Certificate2 certificate = localCertificate();

        using SslStream stream = new(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            userCertificateValidationCallback: PeerHandshake.Validator(
                rules, trust, remote, clock.UtcNow, result => observed = result));

        try
        {
            await stream.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
                deadline).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is AuthenticationException or IOException or SocketException
                or ObjectDisposedException or OperationCanceledException)
        {
            // A caller that connected and then said nothing is refused like any other: the
            // deadline is this host's answer, not an error to report upwards.
            return new ServedConnection(observed, remote, Served: false);
        }

        // Checked again rather than inferred from the handshake not throwing. Under TLS 1.3
        // the server finishes its half before the client's certificate is processed, and
        // Schannel surfaces a rejected client certificate on the first read rather than out of
        // AuthenticateAsServerAsync — so "it did not throw" is a platform behaviour, not a
        // guarantee. The decision is PeerIdentity's, and this is where it is enforced.
        if (observed != PeerVerdict.Accepted)
        {
            return new ServedConnection(observed, remote, Served: false);
        }

        // Nothing to serve is silence, not an empty snapshot: a caller must never read
        // "this host has no VMs" from "this host has not published yet".
        if (snapshotStore.Read(snapshotPath) is not { } snapshot)
        {
            return new ServedConnection(observed, remote, Served: false);
        }

        byte[] payload = Encoding.UTF8.GetBytes(SnapshotWireFormat.Write(snapshot));

        try
        {
            await stream.WriteAsync(payload, deadline).ConfigureAwait(false);
            await stream.FlushAsync(deadline).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or ObjectDisposedException
                or OperationCanceledException)
        {
            // The caller hung up mid-write — a client that refused *our* certificate does
            // exactly this. It is not a reason to throw at whoever is running the listener.
            return new ServedConnection(observed, remote, Served: false);
        }

        return new ServedConnection(observed, remote, Served: true);
    }

    public ValueTask DisposeAsync()
    {
        this.listener?.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// The service loop: accept, serve, hang up, repeat. One connection at a time, and a refused
/// or failed connection never stops the loop — a probe on the port must not take the listener
/// down with it.
public sealed class LoopingPeerListener(
    Func<string, X509Certificate2> localCertificate,
    PeerTrust trust,
    ISnapshotStore snapshotStore,
    IClock clock,
    Action<ServedConnection>? observe = null) : IPeerListener
{
    public async Task RunAsync(
        ListenerSettings settings, PeerRules rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.LocalCertificateThumbprint is not { } thumbprint)
        {
            throw new InvalidOperationException(
                "the listener is enabled without a local certificate thumbprint");
        }

        await using SnapshotListener listener =
            new(() => localCertificate(thumbprint), trust, snapshotStore, clock);

        listener.Start(IPAddress.Any, settings.Port);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // The wait for a connection is bounded by the service's own token only. The
                // per-connection deadline starts inside, once there is a caller to hold it.
                observe?.Invoke(await listener
                    .ServeOneAsync(settings.SnapshotPath, rules, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or IOException or SocketException
                    or AuthenticationException)
            {
                // A slow or bad connection is not a reason to stop serving the good ones.
                // Only the service's own token ends the loop.
            }
        }
    }
}
