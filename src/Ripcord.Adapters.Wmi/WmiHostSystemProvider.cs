using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
using Ripcord.Domain.Inventory;
using Ripcord.Ports.Hosts;

namespace Ripcord.Adapters.Wmi;

/// The host outside Hyper-V, from three CIM sources in two namespaces (COHERENCE B4). Like
/// every adapter here it cannot be run off Windows, so it decides nothing: which volume
/// matters, and what a BitLocker-without-auto-unlock means, are the Domain's.
///
/// Each source is read independently. A host with no BitLocker at all has no
/// `MicrosoftVolumeEncryption` namespace to query, and losing the free space and the installed
/// memory over that would be the adapter deciding the whole reading had failed.
public sealed class WmiHostSystemProvider(TimeSpan timeout) : IHostSystemProvider
{
    private const string CimV2 = @"root\cimv2";

    private const string EncryptionNamespace = @"root\cimv2\Security\MicrosoftVolumeEncryption";

    /// V10: whether this reports installed or usable memory is not settled. Suspected usable,
    /// which is the figure the feasibility calculation wants — verify on the real host before
    /// trusting a calculation that sits within a gigabyte of the limit.
    private const string MemoryQuery = "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem";

    /// DriveType 3 is a local fixed disk. Without the filter this returns the DVD drive and
    /// every mapped share, each with a free space the rules would compare against.
    private const string VolumeQuery =
        "SELECT DeviceID, FreeSpace, Size FROM Win32_LogicalDisk WHERE DriveType = 3";

    private const string EncryptableVolumeQuery =
        "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume";

    /// `ProtectionStatus`: 0 off, 1 on, 2 unknown. Only 1 is protection; 2 is not "no".
    private const ushort ProtectionOn = 1;

    public Task<HostSystemReading> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => this.Read(cancellationToken), cancellationToken);

    private HostSystemReading Read(CancellationToken cancellationToken)
    {
        CimOperationOptions options =
            new() { Timeout = timeout, CancellationToken = cancellationToken };

        using CimSession session = CimSession.Create(computerName: null);

        Dictionary<string, EncryptionState> encryption = ReadEncryption(session, options);

        return new HostSystemReading(
            ReadPhysicalRamMb(session, options),
            ReadVolumes(session, options, encryption));
    }

    private static int? ReadPhysicalRamMb(CimSession session, CimOperationOptions options)
    {
        foreach (CimInstance instance in Query(session, CimV2, MemoryQuery, options))
        {
            using (instance)
            {
                if (CimValues.Size(instance, "TotalPhysicalMemory") is { } bytes)
                {
                    return (int)(bytes / (1024 * 1024));
                }
            }
        }

        return null;
    }

    private static List<HostVolume> ReadVolumes(
        CimSession session,
        CimOperationOptions options,
        Dictionary<string, EncryptionState> encryption)
    {
        List<HostVolume> volumes = [];

        foreach (CimInstance instance in Query(session, CimV2, VolumeQuery, options))
        {
            using (instance)
            {
                if (CimValues.Text(instance, "DeviceID") is not { Length: > 0 } deviceId)
                {
                    continue;
                }

                EncryptionState state = encryption.GetValueOrDefault(
                    deviceId.ToUpperInvariant(), EncryptionState.Unread);

                volumes.Add(new HostVolume(
                    deviceId,
                    CimValues.Size(instance, "FreeSpace"),
                    CimValues.Size(instance, "Size"),
                    state.IsProtected,
                    state.IsAutoUnlockEnabled));
            }
        }

        return volumes;
    }

    /// Keyed by drive letter — `Win32_EncryptableVolume.DriveLetter` is "D:", the same
    /// spelling `Win32_LogicalDisk.DeviceID` uses.
    private static Dictionary<string, EncryptionState> ReadEncryption(
        CimSession session, CimOperationOptions options)
    {
        Dictionary<string, EncryptionState> states = new(StringComparer.Ordinal);

        foreach (CimInstance instance in Query(
            session, EncryptionNamespace, EncryptableVolumeQuery, options))
        {
            using (instance)
            {
                if (CimValues.Text(instance, "DriveLetter") is not { Length: > 0 } letter)
                {
                    continue;
                }

                bool? isProtected = CimValues.Number(instance, "ProtectionStatus") is { } status
                    ? status == ProtectionOn
                    : null;

                states[letter.ToUpperInvariant()] = new EncryptionState(
                    isProtected, ReadAutoUnlock(session, instance, options));
            }
        }

        return states;
    }

    /// V12: auto-unlock is a method with an out parameter, not a property — there is nothing
    /// to select in the query. A provider that does not expose it leaves the answer unknown,
    /// which the rule reports as unevaluable rather than as "does not unlock".
    private static bool? ReadAutoUnlock(
        CimSession session, CimInstance volume, CimOperationOptions options)
    {
        try
        {
            using CimMethodResult result = session.InvokeMethod(
                EncryptionNamespace, volume, "IsAutoUnlockEnabled", null, options);

            return result.OutParameters["IsAutoUnlockEnabled"]?.Value as bool?;
        }
        catch (CimException)
        {
            return null;
        }
    }

    /// A namespace that is not there is an answer, not a failure: a host with BitLocker never
    /// installed has no encryption namespace, and the memory reading must survive that.
    private static IEnumerable<CimInstance> Query(
        CimSession session,
        string namespaceName,
        string query,
        CimOperationOptions options)
    {
        try
        {
            return [.. session.QueryInstances(namespaceName, "WQL", query, options)];
        }
        catch (CimException)
        {
            return [];
        }
    }

    private readonly record struct EncryptionState(bool? IsProtected, bool? IsAutoUnlockEnabled)
    {
        public static EncryptionState Unread => new(null, null);
    }
}
