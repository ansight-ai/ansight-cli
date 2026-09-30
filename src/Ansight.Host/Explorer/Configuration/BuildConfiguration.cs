using System.Reflection;

namespace Ansight.Host.Explorer.Configuration;

internal static class BuildConfiguration
{
    private const string MapboxAccessTokenMetadataKey = "AnsightMapboxAccessToken";

    public static string MapboxAccessToken { get; } = typeof(BuildConfiguration)
        .Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => string.Equals(
            attribute.Key,
            MapboxAccessTokenMetadataKey,
            StringComparison.Ordinal))?
        .Value?
        .Trim() ?? string.Empty;
}
