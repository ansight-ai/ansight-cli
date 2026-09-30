namespace Ansight.Infrastructure.Extensions;

/// <summary>An optional module creates services for the requesting host or CLI context.</summary>
public interface IOptionalExtension : IDisposable
{
    object GetService(Type contract);
}

public interface ILoadedExtensionServices
{
    object? TryGetLoadedService(Type contract);
}
