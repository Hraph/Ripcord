using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Ripcord.Domain.Deployment;

/// The SID Windows gives `NT SERVICE\<name>`: S-1-5-80 and the SHA-1 of the upper-cased name,
/// read as five little-endian integers. Computed, so a plan can name it before the account
/// resolves; the adapter checks it against Windows before writing anything with it.
public static class VirtualAccount
{
    public static string Sid(string serviceName)
    {
        ArgumentNullException.ThrowIfNull(serviceName);

        // SHA-1 because that is how Windows derives it: an identifier, not a security choice.
#pragma warning disable CA5350
        byte[] hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
#pragma warning restore CA5350

        return "S-1-5-80-" + string.Join(
            '-',
            Enumerable.Range(0, 5).Select(index =>
                BitConverter.ToUInt32(hash, index * 4).ToString(CultureInfo.InvariantCulture)));
    }
}
