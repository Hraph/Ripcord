namespace Ripcord.Domain;

/// Decision D7. A scheduled task has to tell "the infrastructure is in a bad state" from
/// "the tool itself failed", so the codes never overlap. An unreachable peer is neither:
/// it is a degraded state, and it exits Success.
public enum ExitCode
{
    Success = 0,
    CriticalFinding = 1,
    InvalidConfiguration = 2,
    LocalAccessFailure = 3,
}
