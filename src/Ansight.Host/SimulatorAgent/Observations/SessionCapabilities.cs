using System.Text.Json.Nodes;

namespace Ansight.Host.SimulatorAgent.Observations;

internal sealed record SessionCapabilities(
    string RuntimePlatform,
    IReadOnlyList<string> VisualTreeToolIds,
    IReadOnlyList<string> NavigationFrameworks)
{
    public bool IsDeviceOnly { get; init; }

    private static readonly IReadOnlyList<string> supportedVisualTreeToolIds =
    [
        VisualTreeContract.MauiToolId,
        VisualTreeContract.ReactShadowToolId,
        VisualTreeContract.ReactComponentToolId,
        VisualTreeContract.FlutterToolId,
        VisualTreeContract.DomToolId,
        VisualTreeContract.NativeToolId
    ];

    public static SessionCapabilities Empty(string? platform = null)
        => new(
            VisualTreeContract.NormalizeRuntimePlatform(platform),
            [],
            []);

    public JsonObject CreateInitialObservationArguments()
    {
        var arguments = new JsonObject { ["root"] = VisualTreeContract.CurrentPageRootScope };
        // Published framework trees carry the page and automation IDs used by tasks.
        // With no published provider, retain the normal device-accessibility fallback.
        if (VisualTreeToolIds.FirstOrDefault() is { } toolId)
        {
            arguments["toolId"] = toolId;
        }
        return arguments;
    }

    public static SessionCapabilities FromPublishedTools(
        string? platform,
        JsonObject? catalog)
    {
        var normalizedPlatform = VisualTreeContract.NormalizeRuntimePlatform(platform);
        var publishedToolIds = ReadPublishedExecutableToolIds(catalog);
        var visualTreeToolIds = supportedVisualTreeToolIds
            .Where(publishedToolIds.Contains)
            .ToArray();
        var navigationFrameworks = ResolveNavigationFrameworks(
            normalizedPlatform,
            publishedToolIds);

        return new SessionCapabilities(
            normalizedPlatform,
            visualTreeToolIds,
            navigationFrameworks);
    }

    public string PlatformDisplayName => RuntimePlatform switch
    {
        VisualTreeContract.AndroidRuntimePlatform => "Android",
        VisualTreeContract.IosRuntimePlatform => "iOS",
        VisualTreeContract.MacCatalystRuntimePlatform => "Mac Catalyst",
        VisualTreeContract.MacOsRuntimePlatform => "macOS",
        VisualTreeContract.WebRuntimePlatform => "web",
        VisualTreeContract.WindowsRuntimePlatform => "Windows",
        VisualTreeContract.LinuxRuntimePlatform => "Linux",
        _ => "current platform"
    };

    public IReadOnlyList<string> TechnologyDisplayNames
    {
        get
        {
            var technologies = new List<string>();
            if (VisualTreeToolIds.Contains(VisualTreeContract.MauiToolId, StringComparer.Ordinal)
                || NavigationFrameworks.Contains("maui", StringComparer.Ordinal))
            {
                technologies.Add(".NET MAUI");
            }
            if (VisualTreeToolIds.Contains(VisualTreeContract.ReactShadowToolId, StringComparer.Ordinal)
                || VisualTreeToolIds.Contains(VisualTreeContract.ReactComponentToolId, StringComparer.Ordinal)
                || NavigationFrameworks.Contains("react-native", StringComparer.Ordinal))
            {
                technologies.Add("React Native");
            }
            if (VisualTreeToolIds.Contains(VisualTreeContract.FlutterToolId, StringComparer.Ordinal)
                || NavigationFrameworks.Contains("flutter", StringComparer.Ordinal))
            {
                technologies.Add("Flutter");
            }
            if (VisualTreeToolIds.Contains(VisualTreeContract.DomToolId, StringComparer.Ordinal))
            {
                technologies.Add("DOM");
            }

            return technologies;
        }
    }

    public string ProfileDescription
    {
        get
        {
            var technologies = TechnologyDisplayNames;
            return technologies.Count == 0
                ? PlatformDisplayName
                : $"{PlatformDisplayName} with {string.Join(" and ", technologies)}";
        }
    }

    private static HashSet<string> ReadPublishedExecutableToolIds(JsonObject? catalog)
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        if (catalog?["tools"] is not JsonArray tools)
        {
            return results;
        }

        foreach (var tool in tools.OfType<JsonObject>())
        {
            var toolId = ReadString(tool, "id") ?? ReadString(tool, "name");
            var executable = tool["executable"] is not JsonValue executableValue
                             || !executableValue.TryGetValue<bool>(out var parsedExecutable)
                             || parsedExecutable;
            if (toolId is not null
                && executable
                && tool["denial"] is null)
            {
                results.Add(toolId);
            }
        }

        return results;
    }

    private static bool IsFrameworkCompatibleWithPlatform(
        string framework,
        string runtimePlatform)
    {
        if (runtimePlatform == VisualTreeContract.UnknownRuntimePlatform)
        {
            return true;
        }

        return framework switch
        {
            "ios-uikit" or "ios-swiftui" => runtimePlatform == VisualTreeContract.IosRuntimePlatform,
            "maccatalyst-uikit" or "maccatalyst-swiftui" =>
                runtimePlatform == VisualTreeContract.MacCatalystRuntimePlatform,
            "macos-appkit" or "macos-swiftui" => runtimePlatform == VisualTreeContract.MacOsRuntimePlatform,
            "android-views" or "android-compose" => runtimePlatform == VisualTreeContract.AndroidRuntimePlatform,
            _ => true
        };
    }

    private static IReadOnlyList<string> ResolveNavigationFrameworks(
        string runtimePlatform,
        IReadOnlySet<string> publishedToolIds)
    {
        var claimedToolIds = new HashSet<string>(StringComparer.Ordinal);
        var frameworks = new List<string>();
        foreach (var controller in NavigationControllerCatalog.All)
        {
            if (!IsFrameworkCompatibleWithPlatform(controller.Framework, runtimePlatform))
            {
                continue;
            }

            var toolId = controller.NavigationToolIds.FirstOrDefault(candidate =>
                publishedToolIds.Contains(candidate)
                && !claimedToolIds.Contains(candidate));
            if (toolId is null)
            {
                continue;
            }

            claimedToolIds.Add(toolId);
            frameworks.Add(controller.Framework);
        }

        return frameworks;
    }

    private static string? ReadString(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue jsonValue
           && jsonValue.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
