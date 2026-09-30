using System.Reflection;

namespace Ansight.Host.Telemetry;

internal static class PostHogBuildConfiguration
{
    public static string ProjectToken { get; } = typeof(PostHogBuildConfiguration)
        .Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => string.Equals(
            attribute.Key,
            "AnsightPostHogProjectToken",
            StringComparison.Ordinal))?
        .Value?
        .Trim() ?? string.Empty;
}
