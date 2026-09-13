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

    /// Nothing was changed: a precondition refused, or the operator interrupted the run.
    /// That is what separates it from a failure part-way through.
    Refused = 4,

    /// A mutating operation failed and left the host in neither the state it started in nor
    /// the one it was going to. A human has to look. The table gave this to milestone 4, but
    /// a test failover whose cleanup fails leaves an orphaned test VM holding disk — which is
    /// this state exactly, so milestone 3 reaches it first.
    IntermediateState = 5,
}
