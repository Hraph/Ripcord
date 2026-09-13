using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Configuration;

/// The per-VM key that keeps one machine out of the sweeps (decision D19). Optional, because
/// the safe reading of an absent key is "this VM fails over like the rest"; but a *present*
/// misspelling is refused rather than defaulted, for the reason the priority names are —
/// silently reading `manaul` as `auto` sweeps up the machine the key was written to exclude.
public class FailoverPolicyValidationTests
{
    [Fact]
    public void An_absent_key_means_the_vm_is_swept_like_the_rest()
    {
        RipcordConfiguration configuration = Configurations.Create();

        Assert.All(
            configuration.Vms.Where(vm => vm.Name != "VM-BACKUP-01"),
            vm => Assert.Equal(FailoverPolicy.Auto, vm.Failover));
    }

    [Theory]
    [InlineData("auto", FailoverPolicy.Auto)]
    [InlineData("manual", FailoverPolicy.Manual)]
    [InlineData("never", FailoverPolicy.Never)]
    [InlineData("MANUAL", FailoverPolicy.Manual)]
    public void The_policy_is_read_by_name_case_insensitively(string written, FailoverPolicy expected)
    {
        RipcordConfiguration configuration =
            Configurations.Create(document => document.Vms![2].Failover = written);

        Assert.Equal(expected, configuration.Vms[2].Failover);
    }

    [Theory]
    [InlineData("manaul")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("yes")]
    public void A_policy_outside_the_declared_set_is_rejected(string written)
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Vms![2].Failover = written;

        ConfigurationValidation result =
            ConfigurationValidator.Validate(document, ValidDocument.MachineName);

        Assert.Contains(result.Errors, error => error.Path == "vms[2].failover");
    }

    /// An empty value is a key somebody started typing and left. Refused for the same reason.
    [Fact]
    public void A_blank_policy_is_rejected_rather_than_treated_as_absent()
    {
        ConfigurationDocument document = ValidDocument.Create();
        document.Vms![2].Failover = "   ";

        ConfigurationValidation result =
            ConfigurationValidator.Validate(document, ValidDocument.MachineName);

        Assert.Contains(result.Errors, error => error.Path == "vms[2].failover");
    }
}
