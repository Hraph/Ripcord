using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Ripcord.Adapters.Pairing.Wire;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;
using Ripcord.Ports;
using Ripcord.Ports.Pairing;

namespace Ripcord.Adapters.Pairing.Transport;

/// The client half of the pair channel. It connects, presents this host's certificate, checks
/// the peer's, reads one payload and hangs up. There is no request to write: the protocol has
/// no verb, so there is nothing to abuse.
public sealed class MutualTlsPeerChannel(
    Func<string, X509Certificate2> localCertificate, PeerTrust trust, IClock clock)
    : IPeerChannel
{
    public async Task<PeerFetch> FetchAsync(
        PeerEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        using CancellationTokenSource attempt =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(endpoint.Timeout);

        try
        {
            return await this.FetchOnceAsync(endpoint, attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The peer may be dead, or merely slow. Either way this host waited long enough.
            return PeerFetch.Silent(HostReachability.TimedOut(clock.UtcNow));
        }
        catch (SocketException exception)
            when (exception.SocketErrorCode == SocketError.ConnectionRefused)
        {
            // Distinct from a timeout on purpose: refused means the host is up and the
            // listener is not, which is a different thing to go and fix.
            return PeerFetch.Silent(HostReachability.Refused(clock.UtcNow));
        }
        catch (Exception exception) when (
            exception is SocketException or IOException or AuthenticationException)
        {
            return PeerFetch.Silent(HostReachability.Failed(exception.Message, clock.UtcNow));
        }
    }

    private async Task<PeerFetch> FetchOnceAsync(
        PeerEndpoint endpoint, CancellationToken cancellationToken)
    {
        using TcpClient client = new();
        await client.ConnectAsync(endpoint.Address, endpoint.Port, cancellationToken)
            .ConfigureAwait(false);

        using X509Certificate2 certificate =
            localCertificate(endpoint.LocalCertificateThumbprint);

        using SslStream stream = new(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            userCertificateValidationCallback: PeerHandshake.Validator(
                endpoint.Rules, trust, endpoint.Address, clock.UtcNow, _ => { }));

        await stream.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                // The address, not the expected subject: this is the SNI name, and the name
                // that actually identifies the peer is checked in the Domain.
                TargetHost = endpoint.Address,
                    ClientCertificates = [certificate],
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
            cancellationToken).ConfigureAwait(false);

        string? payload = await ReadCappedAsync(stream, cancellationToken).ConfigureAwait(false);

        if (payload is null)
        {
            return PeerFetch.Silent(HostReachability.Failed(
                "the peer sent more than the payload cap allows", clock.UtcNow));
        }

        return SnapshotWireFormat.Read(payload) is { } snapshot
            ? PeerFetch.Answered(snapshot)
            : PeerFetch.Silent(HostReachability.Failed(
                "the peer sent something this version cannot read", clock.UtcNow));
    }

    /// The peer is trusted to be the peer, not to be well behaved: a compromised one could
    /// stream forever. Over the cap is refused outright rather than truncated — a truncated
    /// payload would be reported as "unreadable", which hides what actually happened.
    private static async Task<string?> ReadCappedAsync(
        Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[SnapshotWireFormat.MaxPayloadBytes + 1];
        int total = 0;

        while (total < buffer.Length)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total > SnapshotWireFormat.MaxPayloadBytes
            ? null
            : System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }
}
