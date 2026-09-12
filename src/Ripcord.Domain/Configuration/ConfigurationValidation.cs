namespace Ripcord.Domain.Configuration;

/// Either a configuration or the reasons there is none. Never both.
public sealed record ConfigurationValidation(
    RipcordConfiguration? Configuration,
    IReadOnlyList<ConfigurationError> Errors)
{
    public static ConfigurationValidation Valid(RipcordConfiguration configuration) =>
        new(configuration, []);

    public static ConfigurationValidation Invalid(IReadOnlyList<ConfigurationError> errors) =>
        new(null, errors);
}
