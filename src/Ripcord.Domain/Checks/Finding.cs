using Ripcord.Domain.Configuration;

namespace Ripcord.Domain.Checks;

/// Violated, or unanswerable. There is no "satisfied" finding: a rule that holds produces
/// nothing, and a rule whose data is missing produces an Unevaluable one — never silence,
/// because silence reads as success and a reassuring false negative is the worst outcome
/// this tool can produce.
public enum FindingVerdict
{
    Violated,
    Unevaluable,
}

/// An acknowledgement matched against a finding. Lapsed is carried rather than dropped: the
/// operator has to see that the reason they accepted this expired, not simply watch the
/// finding reappear with no explanation.
public sealed record Suppression(Acknowledgement Acknowledgement, bool IsActive);

/// What was observed, what it means **on the day of the failover**, and the command that
/// fixes it — never executed. The second field is the one that matters: "incorrect vSwitch"
/// helps nobody, "this VM will boot with no network on the target" triggers action.
///
/// Subject is the VM the finding is about, and null for a host-level one. It is also the
/// acknowledgement scope, which is why a host-level finding names its host in the text
/// rather than in this field.
public sealed record Finding(
    CheckRule Rule,
    string? Subject,
    string Observed,
    string Implication,
    string? Remedy,
    FindingVerdict Verdict,
    Suppression? Suppression = null)
{
    /// Only a violated critical rule with an *active* acknowledgement is excused. An
    /// unevaluable rule never sets the exit code: code 1 means "a critical rule is violated"
    /// (decision D7), and overloading it with "could not be checked" would make `check`
    /// unusable as the gate milestones 3 to 5 depend on.
    public bool CountsAsCritical =>
        this.Verdict == FindingVerdict.Violated
        && this.Rule.Severity == Severity.Critical
        && this.Suppression is not { IsActive: true };
}

/// Builders, so a rule reads as the sentence it is rather than as seven constructor
/// arguments. The unevaluable wording is written once here: every rule says the same thing
/// about missing data, and it must never sound like a pass.
internal static class Found
{
    public static Finding Violated(
        string ruleId, string? subject, string observed, string implication, string? remedy) =>
        new(Rule(ruleId), subject, observed, implication, remedy, FindingVerdict.Violated);

    public static Finding Unevaluable(string ruleId, string? subject, string reason) =>
        new(
            Rule(ruleId),
            subject,
            reason,
            "this rule could not be checked; treat it as unverified, not as satisfied",
            null,
            FindingVerdict.Unevaluable);

    private static CheckRule Rule(string ruleId) =>
        CheckRules.ById(ruleId)
            ?? throw new InvalidOperationException($"no rule is registered as '{ruleId}'");
}
