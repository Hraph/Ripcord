namespace Ripcord.Ports.Alerting;

/// Resolves the name of a secret to its value. The name is in `ripcord.yaml`; the value is
/// not, and never is (decision D24).
public interface ISecretStore
{
    /// Null when the host has no such secret. The caller reports that as a delivery failure
    /// rather than guessing at an empty password.
    string? Find(string name);
}
