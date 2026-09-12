using Ripcord.Domain.Inventory;

namespace Ripcord.Ports.Hosts;

/// The Windows certificate store, by thumbprint (decision D6). `X509Store`, not WMI — the
/// "never `powershell.exe`" rule holds, and a subject lookup can return several certificates
/// including an expired one and pick the wrong one.
///
/// Synchronous because the store is: pretending otherwise would add a state machine around a
/// memory read.
public interface ICertificateProvider
{
    /// Null when nothing in the store carries that thumbprint. That is a finding for the
    /// rules to report, not an exception for the command to fail on.
    CertificateFact? Find(string thumbprint);
}
