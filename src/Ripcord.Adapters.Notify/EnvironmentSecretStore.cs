using Ripcord.Ports.Alerting;

namespace Ripcord.Adapters.Notify;

/// Secrets from the process environment. `ripcord.yaml` names the variable; the service
/// account's environment holds the value, which keeps the relay password out of the
/// configuration directory and out of every backup of it (decision D24).
public sealed class EnvironmentSecretStore : ISecretStore
{
    public string? Find(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}
