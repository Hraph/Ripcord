using Ripcord.Adapters.Fake;
using Ripcord.Application;
using Ripcord.Domain.Configuration;
using Ripcord.Domain.Inventory;
using Ripcord.Domain.Replication;
using Ripcord.Ports.Hosts;
using Ripcord.Tests.Configuration;

namespace Ripcord.Tests.Application;

/// The local host as one value: Hyper-V's view of its VMs, plus the host-system facts the
/// cross-host rules compare. Three ports, three failure modes, and only one of them may fail
/// the command — a host that cannot read its own Hyper-V has nothing to say, while a host
/// that cannot read its own free space still has plenty.
public class LocalStateReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_three_ports_compose_into_one_host_state()
    {
        LocalRead read = Read();

        HostState state = Assert.IsType<HostState>(read.State);

        Assert.Empty(read.Notes);
        Assert.Equal(12_288, state.Facts!.PhysicalRamMb);
        Assert.Equal(500_000_000_000, state.Facts.Volume("D:")!.FreeBytes);
        Assert.Equal("CN=HV-REPLICA-01", state.Facts.Certificate!.CommonName);
    }

    /// WMI down, privileges missing, a timeout on our own host: its own exit code, and no
    /// partly built state to render.
    [Fact]
    public void A_hyperv_failure_yields_no_state_and_a_reason()
    {
        LocalRead read = Read(hyperv: FakeHypervProvider.FailingLocally("WMI is not running"));

        Assert.Null(read.State);
        Assert.Contains("WMI is not running", read.FailureMessage);
    }

    /// Graceful degradation: the VMs are still worth showing, and the rules that needed the
    /// host facts report that they could not be evaluated rather than that all is well.
    [Fact]
    public void A_host_system_failure_degrades_to_unknown_facts_and_says_why()
    {
        LocalRead read = Read(hostSystem: FakeHostSystemProvider.Failing("cimv2 unavailable"));

        Assert.NotNull(read.State);
        Assert.Null(read.FailureMessage);
        Assert.Null(read.State.Facts!.PhysicalRamMb);
        Assert.Empty(read.State.Facts.Volumes);
        Assert.Contains(read.Notes, note => note.Contains("cimv2 unavailable"));
    }

    [Fact]
    public void A_certificate_store_failure_leaves_the_rest_of_the_facts_intact()
    {
        LocalRead read = Read(certificates: FakeCertificateProvider.Failing("store locked"));

        Assert.Equal(12_288, read.State!.Facts!.PhysicalRamMb);
        Assert.Null(read.State.Facts.Certificate);
        Assert.Contains(read.Notes, note => note.Contains("store locked"));
    }

    /// A thumbprint that names nothing in the store is a finding for the rules, not a note
    /// about the tool: the operator configured a certificate that is not there.
    [Fact]
    public void A_thumbprint_matching_nothing_yields_no_certificate_and_no_note()
    {
        LocalRead read = Read(certificates: new FakeCertificateProvider());

        Assert.Null(read.State!.Facts!.Certificate);
        Assert.Empty(read.Notes);
    }

    /// With the listener off there is no configured thumbprint to look up, so the store is
    /// never opened — the certificate rule simply has nothing to judge.
    [Fact]
    public void A_node_with_no_listener_never_opens_the_certificate_store()
    {
        RecordingCertificateProvider certificates = new();

        LocalRead read = Read(
            configuration: Configurations.Create(document => document.Listener!.Enabled = false),
            certificates: certificates);

        Assert.Null(read.State!.Facts!.Certificate);
        Assert.Empty(certificates.Requested);
        Assert.Empty(read.Notes);
    }

    private static LocalRead Read(
        RipcordConfiguration? configuration = null,
        FakeHypervProvider? hyperv = null,
        FakeHostSystemProvider? hostSystem = null,
        ICertificateProvider? certificates = null)
    {
        RipcordConfiguration used = configuration ?? Configurations.Create();

        LocalStateReader reader = new(
            hyperv ?? new FakeHypervProvider(FakeScenarios.Healthy(Now)),
            hostSystem ?? FakeHostSystemProvider.Target(),
            certificates ?? FakeCertificateProvider.Valid(
                ValidDocument.LocalThumbprint, "CN=HV-REPLICA-01"));

        return reader.ReadAsync(used, CancellationToken.None).GetAwaiter().GetResult();
    }

    private sealed class RecordingCertificateProvider : ICertificateProvider
    {
        public List<string> Requested { get; } = [];

        public CertificateFact? Find(string thumbprint)
        {
            this.Requested.Add(thumbprint);
            return null;
        }
    }
}
