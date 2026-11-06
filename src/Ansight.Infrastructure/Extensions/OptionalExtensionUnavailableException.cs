namespace Ansight.Infrastructure.Extensions;

public sealed class OptionalExtensionUnavailableException : InvalidOperationException
{
    public OptionalExtensionUnavailableException(string module)
        : base($"This feature requires the optional {module} component. Local developer features work without it.")
    {
    }
}
