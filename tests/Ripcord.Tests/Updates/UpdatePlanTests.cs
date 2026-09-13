using Ripcord.Domain;
using Ripcord.Domain.Checks;
using Ripcord.Domain.Failover;
using Ripcord.Domain.Updates;

namespace Ripcord.Tests.Updates;

/// Deciding is separate from doing, the same way `deploy-listener` separates them. Everything
/// here is pure: what the command would do, and what it refuses to do, with no download, no
/// file and no host.
public sealed class UpdatePlanTests
{
    private const string Peer = "HV-PRIMARY-01";

    [Fact]
    public void A_host_already_on_the_latest_release_has_nothing_to_do()
    {
        UpdatePlan plan = UpdatePlan.For(Subject(UpdateStatus.Between("0.2.0", "0.2.0")));

        Assert.False(plan.ChangesAnything);
        Assert.Null(plan.Halt);
    }

    [Fact]
    public void A_newer_release_is_downloaded_then_verified_then_installed_in_that_order()
    {
        UpdatePlan plan = UpdatePlan.For(Subject(UpdateStatus.Between("0.1.0", "0.2.0")));

        Assert.Null(plan.Halt);
        Assert.Equal(
            [
                UpdateAction.Download,
                UpdateAction.Verify,
                UpdateAction.DiscardPrevious,
                UpdateAction.SetAside,
                UpdateAction.Install,
            ],
            plan.Steps.Select(step => step.Action));
    }

    /// Verifying after installing would be verifying nothing. The order is the security
    /// property, so it is asserted rather than assumed.
    [Fact]
    public void Nothing_is_set_aside_before_the_signature_has_been_checked()
    {
        UpdatePlan plan = UpdatePlan.For(Subject(UpdateStatus.Between("0.1.0", "0.2.0")));

        int verify = Position(plan, UpdateAction.Verify);

        Assert.True(verify < Position(plan, UpdateAction.SetAside));
        Assert.True(verify < Position(plan, UpdateAction.Install));
    }

    [Fact]
    public void Every_step_says_what_it_does_and_why()
    {
        UpdatePlan plan = UpdatePlan.For(Subject(UpdateStatus.Between("0.1.0", "0.2.0")));

        Assert.All(plan.Steps, step =>
        {
            Assert.False(string.IsNullOrWhiteSpace(step.Description));
            Assert.False(string.IsNullOrWhiteSpace(step.Reason));
        });

        Assert.Equal([1, 2, 3, 4, 5], plan.Steps.Select(step => step.Number));
    }

    /// The off switch, and it is refused by name rather than silently doing nothing: a host
    /// somebody believes is updating and is not is the failure this refusal exists to prevent.
    [Fact]
    public void A_host_not_allowed_to_install_is_refused_by_name()
    {
        UpdatePlan plan = UpdatePlan.For(
            Subject(UpdateStatus.Between("0.1.0", "0.2.0"), installAllowed: false));

        Assert.False(plan.ChangesAnything);
        Assert.NotNull(plan.Halt);
        Assert.Contains("updates.install", plan.Halt, StringComparison.Ordinal);
    }

    /// A version that cannot be read cannot be compared, and a binary that replaces itself on
    /// a guess is the one thing worse than not updating.
    [Fact]
    public void A_version_that_cannot_be_compared_halts_rather_than_guessing()
    {
        UpdatePlan plan = UpdatePlan.For(Subject(UpdateStatus.Between("0.1.0", "not-a-version")));

        Assert.False(plan.ChangesAnything);
        Assert.NotNull(plan.Halt);
    }

    /// Decision D54: a failover spanning both hosts is refused while they run different
    /// builds. Updating one host is what creates that disagreement, so the command says so
    /// before it is confirmed — every time, in every pair state.
    [Fact]
    public void Updating_one_host_warns_that_the_pair_will_disagree()
    {
        UpdatePlan plan = UpdatePlan.For(Subject(UpdateStatus.Between("0.1.0", "0.2.0")));

        Assert.Contains(
            plan.Warnings,
            warning => warning.Contains(Peer, StringComparison.Ordinal)
                && warning.Contains("failover", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_pair_that_already_disagrees_says_so_rather_than_repeating_the_general_warning()
    {
        UpdatePlan plan = UpdatePlan.For(Subject(
            UpdateStatus.Between("0.1.0", "0.2.0"),
            skew: VersionSkew.Between(Build("0.1.0"), Build("0.0.9"))));

        Assert.Contains(
            plan.Warnings,
            warning => warning.Contains("already", StringComparison.OrdinalIgnoreCase));
    }

    /// Production is running on the recovery host. The failback is the thing that has to work
    /// next, and this is what would delay it.
    [Fact]
    public void A_failed_over_pair_is_warned_about_the_failback_and_still_allowed()
    {
        UpdatePlan plan = UpdatePlan.For(
            Subject(UpdateStatus.Between("0.1.0", "0.2.0"), mode: OperatingMode.FailedOver));

        Assert.True(plan.ChangesAnything);
        Assert.Null(plan.Halt);

        Assert.Contains(
            plan.Warnings,
            warning => warning.Contains("failed over", StringComparison.OrdinalIgnoreCase)
                || warning.Contains("failback", StringComparison.OrdinalIgnoreCase));
    }

    /// Nothing is warned about when there is nothing to do — a page of consequences for a
    /// command that is going to change nothing is how warnings stop being read.
    [Fact]
    public void A_host_with_nothing_to_do_is_warned_about_nothing()
    {
        UpdatePlan plan = UpdatePlan.For(Subject(UpdateStatus.Between("0.2.0", "0.2.0")));

        Assert.Empty(plan.Warnings);
    }

    private static int Position(UpdatePlan plan, UpdateAction action) =>
        plan.Steps.ToList().FindIndex(step => step.Action == action);

    private static BuildIdentity Build(string version) => new(version, "abc123def456");

    private static UpdateSubject Subject(
        UpdateStatus status,
        bool installAllowed = true,
        VersionSkew? skew = null,
        OperatingMode mode = OperatingMode.Normal) =>
        new(
            status,
            installAllowed,
            skew ?? VersionSkew.Between(Build("0.1.0"), Build("0.1.0")),
            mode,
            Peer);
}
