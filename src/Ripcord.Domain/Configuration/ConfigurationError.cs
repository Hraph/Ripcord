namespace Ripcord.Domain.Configuration;

/// Path is the dotted location in the file, so the operator can go straight to the line.
public sealed record ConfigurationError(string Path, string Message);
