using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Configuration;

/// One well-formed document the validation tests start from, so a test says only what it is
/// about: it breaks a single field and asserts a single error. Kept in one place because a
/// new required section otherwise has to be added to every test file that builds a document.
internal static class ValidDocument
{
    public const string MachineName = "HV-REPLICA-01";
    public const string PeerName = "HV-PRIMARY-01";
    public const string LocalThumbprint = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";
    public const string PeerThumbprint = "0F1E2D3C4B5A69788796A5B4C3D2E1F012345678";

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
            // `manual` as in the shipped configuration (decision D19): it stays replicated and
            // checkable, and no sweep picks it up.
            new VmDocument
            {
                Name = "VM-BACKUP-01",
                Priority = "P2",
                HasPassthroughDisk = true,
                Failover = "manual",
            },
        ],
    };
}
