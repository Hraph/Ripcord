using System.Text;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Serialization;

namespace Ripcord.Adapters.Wmi;

/// A method parameter the MOF declares as `string` with the EmbeddedInstance qualifier takes
/// the instance's text, not the instance: `CimType.Instance` is refused with a type mismatch
/// (observed on the first real host, V38).
internal static class CimEmbeddedInstance
{
    // MI_XML in UTF-16, as Microsoft's Hyper-V CIM samples do it. Unverified on hardware.
    public static string Text(CimInstance instance)
    {
        using CimSerializer serializer = CimSerializer.Create();
        return Encoding.Unicode.GetString(
            serializer.Serialize(instance, InstanceSerializationOptions.None));
    }
}
