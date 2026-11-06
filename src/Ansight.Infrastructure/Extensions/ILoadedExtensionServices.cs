namespace Ansight.Infrastructure.Extensions;

public interface ILoadedExtensionServices
{
    object? TryGetLoadedService(Type contract);
}
