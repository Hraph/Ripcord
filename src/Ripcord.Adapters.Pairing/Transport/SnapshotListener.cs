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
    IClock clock) : IAsyncDisposable
{
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

        string remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
        PeerVerdict verdict = PeerVerdict.NoCertificate;

        PeerVerdict observed = verdict;

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
                    ServerCertificate = localCertificate(),
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is AuthenticationException or IOException or SocketException)
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
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

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
                observe?.Invoke(await listener
                    .ServeOneAsync(settings.SnapshotPath, rules, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (
                exception is IOException or SocketException or AuthenticationException)
            {
                // A bad connection is not a reason to stop serving the good ones.
            }
        }
    }
}
