using Ripcord.Domain.Configuration;

namespace Ripcord.Tests.Configuration;

/// A validated configuration for the tests that need one, built by validating the same
/// document `ValidDocument` describes. Going through the validator rather than constructing
/// the record directly means a test can never assert against a configuration the real
/// validator would have refused.
internal static class Configurations
{
    public static RipcordConfiguration Create(Action<ConfigurationDocument>? adjust = null)
    {
        ConfigurationDocument document = ValidDocument.Create();
        adjust?.Invoke(document);

        ConfigurationValidation validation =
            ConfigurationValidator.Validate(document, ValidDocument.MachineName);

        return validation.Configuration
            ?? throw new InvalidOperationException(
                "the test document is invalid: "
                + string.Join(
                    ", ", validation.Errors.Select(error => $"{error.Path} {error.Message}")));
    }
}
