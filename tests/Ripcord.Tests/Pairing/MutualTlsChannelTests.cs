using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ripcord.Adapters.Fake;
using Ripcord.Adapters.Pairing.Transport;
using Ripcord.Domain.Pairing;
using Ripcord.Domain.Replication;
using Ripcord.Ports;

namespace Ripcord.Tests.Pairing;

/// The handshake end to end, over a real socket, with certificates generated here. No Windows
/// and no Hyper-V is involved, which is the reason this milestone can be built on macOS —
/// and the reason "it refuses the wrong certificate" is a test rather than a claim.
public sealed class MutualTlsChannelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    private readonly CertificateAuthority pairCa = new("Ripcord-Test-CA");
    private readonly CertificateAuthority strangerCa = new("Stranger-CA");

    private readonly List<X509Certificate2> issued = [];

    [Fact]
    public async Task The_peer_certificate_is_accepted_and_the_snapshot_crosses_intact()
    {
        HostSnapshot published = FakeScenarios.PeerSnapshot(Now.AddMinutes(-2));

        PeerFetch fetch = await this.ExchangeAsync(published);

        Assert.Equal(published, fetch.Snapshot);
        Assert.True(fetch.Reachability.IsReachable);
    }

    /// A certificate signed by the pair's own CA is genuine and still not the peer's — both
    /// hosts are signed by that CA, so chain-only would let either impersonate the other.
    [Fact]
    public async Task A_certificate_from_the_pair_ca_that_is_not_the_peers_is_refused()
    {
        X509Certificate2 impostor = this.Issue(this.pairCa, "CN=HV-IMPOSTOR-01");

        PeerFetch fetch = await this.ExchangeAsync(
            FakeScenarios.PeerSnapshot(Now), clientCertificate: impostor);

        Assert.Null(fetch.Snapshot);
    }

    /// And the reverse: the subject the config expects, signed by a CA nobody trusts.
    [Fact]
    public async Task A_certificate_from_an_untrusted_authority_is_refused()
    {
        X509Certificate2 forged = this.Issue(this.strangerCa, "CN=HV-PRIMARY-01");

        PeerFetch fetch = await this.ExchangeAsync(
            FakeScenarios.PeerSnapshot(Now), clientCertificate: forged);

        Assert.Null(fetch.Snapshot);
    }

    [Fact]
    public async Task An_expired_certificate_is_refused()
    {
        X509Certificate2 expired = this.Issue(
            this.pairCa, "CN=HV-PRIMARY-01", notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        PeerFetch fetch = await this.ExchangeAsync(
            FakeScenarios.PeerSnapshot(Now), clientCertificate: expired);

        Assert.Null(fetch.Snapshot);
    }

    /// The right certificate used from somewhere that is not the peer: a stolen key.
    [Fact]
    public async Task The_listener_refuses_the_right_certificate_from_the_wrong_address()
    {
        ServedConnection served = await this.ServeAsync(
            FakeScenarios.PeerSnapshot(Now), listenerExpectsAddress: "192.0.2.99");

        Assert.Equal(PeerVerdict.WrongAddress, served.Verdict);
        Assert.False(served.Served);
    }

    [Fact]
    public async Task The_listener_refuses_a_caller_presenting_the_wrong_certificate()
    {
        ServedConnection served = await this.ServeAsync(
            FakeScenarios.PeerSnapshot(Now),
            clientCertificate: this.Issue(this.pairCa, "CN=HV-IMPOSTOR-01"));

        Assert.Equal(PeerVerdict.WrongCertificate, served.Verdict);
        Assert.False(served.Served);
    }

    /// Nothing published yet is silence, never an empty inventory: a peer must not read "this
    /// host has no VMs" from "this host has not published".
    [Fact]
    public async Task A_listener_with_no_snapshot_serves_nothing_rather_than_an_empty_state()
    {
        ServedConnection served = await this.ServeAsync(publish: null);

        Assert.Equal(PeerVerdict.Accepted, served.Verdict);
        Assert.False(served.Served);
    }

    /// The exit criterion is symmetric, so the tests are too: a listener presenting a
    /// certificate that is not the one the client expects must be refused by the client.
    [Fact]
    public async Task The_client_refuses_a_listener_presenting_the_wrong_certificate()
    {
        PeerFetch fetch = await this.ExchangeAsync(
            FakeScenarios.PeerSnapshot(Now),
            serverCertificate: this.Issue(this.pairCa, "CN=HV-IMPOSTOR-01"));

        Assert.Null(fetch.Snapshot);
    }

    [Fact]
    public async Task The_client_refuses_a_listener_signed_by_an_untrusted_authority()
    {
        PeerFetch fetch = await this.ExchangeAsync(
            FakeScenarios.PeerSnapshot(Now),
            serverCertificate: this.Issue(this.strangerCa, "CN=HV-PRIMARY-01"));

        Assert.Null(fetch.Snapshot);
    }

    /// Nothing is served unless the verdict says so — asserted independently of whether the
    /// TLS stack happened to fail the handshake on this platform.
    [Fact]
    public async Task Nothing_is_ever_served_on_a_verdict_other_than_accepted()
    {
        ServedConnection served = await this.ServeAsync(
            FakeScenarios.PeerSnapshot(Now),
            clientCertificate: this.Issue(this.pairCa, "CN=HV-IMPOSTOR-01"));

        Assert.NotEqual(PeerVerdict.Accepted, served.Verdict);
        Assert.False(served.Served);
    }

    /// Connection refused must stay distinct from a timeout in the rendering: refused means
    /// the host is up and the listener is not, which is a different thing to go and fix.
    [Fact]
    public async Task A_closed_port_reads_as_refused_rather_than_as_a_timeout()
    {
        PeerEndpoint endpoint = this.EndpointFor(ClosedPort());

        PeerFetch fetch = await this.Channel().FetchAsync(endpoint, CancellationToken.None);

        Assert.Null(fetch.Snapshot);
        Assert.Equal(ReachabilityKind.Refused, fetch.Reachability.Kind);
    }

    /// A hung listener must never hang `ripcord status`.
    [Fact]
    public async Task A_peer_that_accepts_and_says_nothing_times_out()
    {
        using TcpSilence silent = new();

        PeerEndpoint endpoint = this.EndpointFor(silent.Port) with
        {
            Timeout = TimeSpan.FromMilliseconds(250),
        };

        PeerFetch fetch = await this.Channel().FetchAsync(endpoint, CancellationToken.None);

        Assert.Null(fetch.Snapshot);
        Assert.Equal(ReachabilityKind.TimedOut, fetch.Reachability.Kind);
    }

    private async Task<PeerFetch> ExchangeAsync(
        HostSnapshot publish,
        X509Certificate2? clientCertificate = null,
        X509Certificate2? serverCertificate = null)
    {
        InMemorySnapshotStore store = new();
        store.Write("state.json", publish);

        await using SnapshotListener listener = this.Listener(store, serverCertificate);
        listener.Start(IPAddress.Loopback, 0);

        Task<ServedConnection> serving = listener.ServeOneAsync(
            "state.json", this.RulesFor("CN=HV-REPLICA-01", "127.0.0.1"), CancellationToken.None);

        PeerFetch fetch = await this.Channel(clientCertificate)
            .FetchAsync(this.EndpointFor(listener.Port), CancellationToken.None);

        await serving;
        return fetch;
    }

    private async Task<ServedConnection> ServeAsync(
        HostSnapshot? publish = null,
        X509Certificate2? clientCertificate = null,
        string listenerExpectsAddress = "127.0.0.1")
    {
        InMemorySnapshotStore store = new();

        if (publish is not null)
        {
            store.Write("state.json", publish);
        }

        await using SnapshotListener listener = this.Listener(store);
        listener.Start(IPAddress.Loopback, 0);

        Task<ServedConnection> serving = listener.ServeOneAsync(
            "state.json",
            this.RulesFor("CN=HV-REPLICA-01", listenerExpectsAddress),
            CancellationToken.None);

        await this.Channel(clientCertificate)
            .FetchAsync(this.EndpointFor(listener.Port), CancellationToken.None);

        return await serving;
    }

    private SnapshotListener Listener(
        InMemorySnapshotStore store, X509Certificate2? serverCertificate = null) =>
        new(
            () => serverCertificate ?? this.Issue(this.pairCa, "CN=HV-PRIMARY-01"),
            new PeerTrust([this.pairCa.RootCertificate]),
            store,
            new FixedClock(Now));

    private MutualTlsPeerChannel Channel(X509Certificate2? clientCertificate = null) =>
        new(
            _ => clientCertificate ?? this.Issue(this.pairCa, "CN=HV-REPLICA-01"),
            new PeerTrust([this.pairCa.RootCertificate]),
            new FixedClock(Now));

    /// The client expects the listener's certificate; the listener expects the client's.
    private PeerEndpoint EndpointFor(int port) =>
        new(
            "127.0.0.1",
            port,
            this.RulesFor("CN=HV-PRIMARY-01", "127.0.0.1"),
            "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555",
            TimeSpan.FromSeconds(5));

    private PeerRules RulesFor(string subject, string address) =>
        new(this.Issue(this.pairCa, subject).Thumbprint, subject, address);

    /// Deterministic per subject, so "the certificate the rules expect" and "the certificate
    /// presented" are the same one without threading instances through every helper.
    private X509Certificate2 Issue(
        CertificateAuthority authority, string subject, DateTimeOffset? notAfter = null)
    {
        X509Certificate2 certificate = authority.Issue(subject, notAfter);
        this.issued.Add(certificate);
        return certificate;
    }

    private static int ClosedPort()
    {
        using System.Net.Sockets.TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        foreach (X509Certificate2 certificate in this.issued)
        {
            certificate.Dispose();
        }

        this.pairCa.Dispose();
        this.strangerCa.Dispose();
    }


    /// Accepts a connection and then says nothing at all, which is what a wedged listener
    /// looks like from the other side.
    private sealed class TcpSilence : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener listener;

        public TcpSilence()
        {
            this.listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            this.listener.Start();
            _ = this.listener.AcceptTcpClientAsync();
        }

        public int Port => ((IPEndPoint)this.listener.LocalEndpoint).Port;

        public void Dispose() => this.listener.Dispose();
    }
}

/// A throwaway CA plus the leaf certificates it signs. Deterministic per subject so the same
/// subject always yields the same key, and therefore the same thumbprint.
internal sealed class CertificateAuthority(string name) : IDisposable
{
    private readonly Dictionary<string, X509Certificate2> issued = [];
    private readonly RSA rootKey = RSA.Create(2048);
    private X509Certificate2? root;

    public X509Certificate2 RootCertificate => this.root ??= BuildRoot(name, this.rootKey);

    public X509Certificate2 Issue(string subject, DateTimeOffset? notAfter)
    {
        string key = $"{subject}|{notAfter}";

        if (this.issued.TryGetValue(key, out X509Certificate2? existing))
        {
            return existing;
        }

        using RSA leafKey = RSA.Create(2048);
        CertificateRequest request = new(
            subject, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false));

        // A leaf may not start before its issuer does, which is what an expired leaf would
        // otherwise ask for.
        DateTimeOffset notBefore = this.RootCertificate.NotBefore.AddMinutes(1);

        using X509Certificate2 signed = request.Create(
            this.RootCertificate,
            notBefore,
            notAfter ?? DateTimeOffset.UtcNow.AddYears(1),
            Guid.NewGuid().ToByteArray());

        X509Certificate2 withKey = X509CertificateLoader.LoadPkcs12(
            signed.CopyWithPrivateKey(leafKey).Export(X509ContentType.Pkcs12), password: null);

        this.issued[key] = withKey;
        return withKey;
    }

    private static X509Certificate2 BuildRoot(string commonName, RSA key)
    {
        CertificateRequest request = new(
            $"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(true, false, 0, true));

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddYears(5));
    }

    public void Dispose()
    {
        foreach (X509Certificate2 certificate in this.issued.Values)
        {
            certificate.Dispose();
        }

        this.root?.Dispose();
        this.rootKey.Dispose();
    }
}
