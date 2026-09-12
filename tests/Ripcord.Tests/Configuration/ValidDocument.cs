using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Configuration;

/// One well-formed document the validation tests start from, so a test says only what it is
/// about: it breaks a single field and asserts a single error. Kept in one place because a
/// new required section otherwise has to be added to every test file that builds a document.
internal static class ValidDocument
{
    public const string MachineName = "HV-REPLICA-01";
    public const string PeerName = "HV-PRIMARY-01";
    public const string LocalThumbprint = "AAAA1111BBBB2222CCCC3333DDDD4444EEEE5555";
    public const string PeerThumbprint = "1111AAAA2222BBBB3333CCCC4444DDDD5555EEEE";

    public static ConfigurationDocument Create() => new()
    {
        SchemaVersion = 1,
        Node = new NodeDocument { Hostname = MachineName, HostMemoryReserveGb = 4 },
        Peer = new PeerDocument
        {
            Hostname = PeerName,
            Address = "192.0.2.11",
            OfflineAfterSec = 120,
        },
        Replication = new ReplicationDocument
        {
            ExpectedRole = "replica",
            ExpectedSwitchName = "vSwitch-PROD",
            ExpectedFrequencySec = 30,
            LagWarningMultiplier = 3,
        },
        Listener = new ListenerDocument
        {
            Enabled = true,
            Port = 7443,
            LocalCertificateThumbprint = LocalThumbprint,
            PeerCertificateThumbprint = PeerThumbprint,
            SnapshotPath = @"D:\Ripcord\state.json",
        },
        Storage = new StorageDocument
        {
            DataVolume = "D:",
            FreeSpaceWarningGb = 200,
            CheckBitlockerAutounlock = true,
        },
        Vms =
        [
            new VmDocument { Name = "VM-DC-01", Priority = "P1", IsDomainController = true },
            new VmDocument { Name = "VM-LEGACY-01", Priority = "P1" },
            new VmDocument { Name = "VM-BACKUP-01", Priority = "P2", HasPassthroughDisk = true },
        ],
    };
}
