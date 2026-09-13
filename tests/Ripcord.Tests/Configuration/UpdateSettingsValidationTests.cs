using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Configuration;

/// The switch that decides whether this binary may replace itself. Off unless the file says
/// otherwise, and refused rather than corrected when the two switches contradict each other.
public class UpdateSettingsValidationTests
{
    [Fact]
    public void A_configuration_with_no_updates_block_neither_looks_nor_installs()
    {
        ConfigurationDocument document = Valid();
        document.Updates = null;

        ConfigurationValidation result = Validate(document);

        Assert.Empty(result.Errors);
        Assert.False(result.Configuration!.Updates.Check);
        Assert.False(result.Configuration.Updates.Install);
    }

    /// Looking is not installing. A host that has been allowed to ask whether a release exists
    /// has not thereby been allowed to replace itself.
    [Fact]
    public void Permission_to_look_is_not_permission_to_install()
    {
        ConfigurationDocument document = Valid();
        document.Updates = new UpdatesDocument { Check = true };

        ConfigurationValidation result = Validate(document);

        Assert.Empty(result.Errors);
        Assert.True(result.Configuration!.Updates.Check);
        Assert.False(result.Configuration.Updates.Install);
    }

    [Fact]
    public void A_host_allowed_to_install_carries_both_switches()
    {
        ConfigurationDocument document = Valid();
        document.Updates = new UpdatesDocument { Check = true, Install = true };

        ConfigurationValidation result = Validate(document);

        Assert.Empty(result.Errors);
        Assert.True(result.Configuration!.Updates.Install);
    }

    /// Installing without looking is not a configuration anybody meant to write: the release
    /// to install is found by looking. Refused by name rather than silently treated as off,
    /// because the operator who wrote it believes this host updates itself.
    [Fact]
    public void Installing_without_permission_to_look_is_refused_by_name()
    {
        ConfigurationDocument document = Valid();
        document.Updates = new UpdatesDocument { Check = false, Install = true };

        ConfigurationValidation result = Validate(document);

        Assert.Contains(result.Errors, error => error.Path == "updates.install");
        Assert.Null(result.Configuration);
    }

    private static ConfigurationDocument Valid() => ValidDocument.Create();

    private static ConfigurationValidation Validate(ConfigurationDocument document) =>
        ConfigurationValidator.Validate(document, ValidDocument.MachineName);
}
