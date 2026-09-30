using System.Text.Json.Nodes;

namespace Ansight.Host.UiAutomation.Navigation;

internal static class NavigationControllerCatalog
{
    private static readonly NavigationController maui = new(
        "maui",
        ["maui.get_navigation_state"],
        [VisualTreeContract.MauiToolId]);

    private static readonly NavigationController reactNative = new(
        "react-native",
        ["react.get_navigation_state"],
        [VisualTreeContract.ReactShadowToolId, VisualTreeContract.ReactComponentToolId]);

    private static readonly NavigationController flutter = new(
        "flutter",
        ["flutter.get_navigation_state"],
        [VisualTreeContract.FlutterToolId]);

    private static readonly NavigationController capacitor = new(
        "capacitor",
        [],
        [VisualTreeContract.DomToolId]);

    private static readonly NavigationController iosUIKit = new(
        "ios-uikit",
        ["ios.uikit.get_navigation_state", "ios.get_navigation_state"],
        [VisualTreeContract.NativeToolId]);

    private static readonly NavigationController iosSwiftUi = new(
        "ios-swiftui",
        ["ios.swiftui.get_navigation_state"],
        [VisualTreeContract.NativeToolId]);

    private static readonly NavigationController macCatalystUIKit = new(
        "maccatalyst-uikit",
        ["ios.uikit.get_navigation_state", "maccatalyst.uikit.get_navigation_state"],
        [VisualTreeContract.NativeToolId]);

    private static readonly NavigationController macCatalystSwiftUi = new(
        "maccatalyst-swiftui",
        ["ios.swiftui.get_navigation_state", "maccatalyst.swiftui.get_navigation_state"],
        [VisualTreeContract.NativeToolId]);

    private static readonly NavigationController macOsAppKit = new(
        "macos-appkit",
        ["macos.appkit.get_navigation_state", "macos.get_navigation_state"],
        [VisualTreeContract.NativeToolId]);

    private static readonly NavigationController macOsSwiftUi = new(
        "macos-swiftui",
        ["macos.swiftui.get_navigation_state"],
        [VisualTreeContract.NativeToolId]);

    private static readonly NavigationController androidViews = new(
        "android-views",
        ["android.views.get_navigation_state", "android.get_navigation_state"],
        [VisualTreeContract.NativeToolId]);

    private static readonly NavigationController androidCompose = new(
        "android-compose",
        ["android.compose.get_navigation_state"],
        [VisualTreeContract.NativeToolId]);

    private static readonly NavigationController genericNative = new(
        "native-unknown",
        [],
        [VisualTreeContract.NativeToolId]);

    public static IReadOnlyList<NavigationController> All { get; } =
    [
        maui,
        reactNative,
        flutter,
        capacitor,
        iosUIKit,
        iosSwiftUi,
        macCatalystUIKit,
        macCatalystSwiftUi,
        macOsAppKit,
        macOsSwiftUi,
        androidViews,
        androidCompose,
        genericNative
    ];

    public static NavigationController? ResolveNavigationTool(string? toolId)
        => string.IsNullOrWhiteSpace(toolId)
            ? null
            : All.FirstOrDefault(controller => controller.NavigationToolIds.Contains(
                toolId,
                StringComparer.Ordinal));

    public static NavigationController? ResolveFramework(string? framework)
        => string.IsNullOrWhiteSpace(framework)
            ? null
            : All.FirstOrDefault(controller => string.Equals(
                controller.Framework,
                framework,
                StringComparison.Ordinal));

    public static IReadOnlyList<NavigationController> ResolveVisualTree(
        JsonObject visualTree,
        string? toolId)
    {
        ArgumentNullException.ThrowIfNull(visualTree);
        if (!string.Equals(toolId, VisualTreeContract.NativeToolId, StringComparison.Ordinal))
        {
            var exact = All.FirstOrDefault(controller => controller.VisualTreeToolIds.Contains(
                toolId ?? string.Empty,
                StringComparer.Ordinal));
            return exact is null ? [] : [exact];
        }

        var platform = VisualTreeContract.NormalizeRuntimePlatform(ReadString(visualTree, "platform"));
        var source = Normalize(ReadString(visualTree, "source"));
        var adapter = Normalize(ReadString(visualTree, "adapter"));
        var typeNames = visualTree["types"] is JsonArray types
            ? types.OfType<JsonValue>()
                .Select(static value => value.TryGetValue<string>(out var typeName) ? typeName : string.Empty)
                .Where(static typeName => typeName.Length > 0)
                .ToArray()
            : [];
        var evidence = string.Join('\n', new[] { source, adapter }.Concat(typeNames)).ToLowerInvariant();
        var results = new List<NavigationController>();

        var hasSwiftUi = ContainsAny(evidence, "swiftui", "uihostingcontroller", "uihostingview", "nshostingcontroller", "nshostingview");
        var hasUIKit = source == "uikit"
                       || ContainsAny(evidence, "apple.uikit", "ios.uikit", "uiwindow", "uiviewcontroller", "uinavigationcontroller", "uitabbarcontroller");
        var hasAppKit = source == "appkit"
                        || ContainsAny(evidence, "apple.appkit", "macos.appkit", "nswindow", "nsviewcontroller", "nstabviewcontroller");
        var hasCompose = source is "compose" or "jetpack-compose"
                         || ContainsAny(evidence, "android.compose", "androidx.compose", "composeview", "androidcomposeview", "semanticsnode");
        var hasAndroidViews = source is "views" or "android-views"
                              || ContainsAny(evidence, "android.views", "android.view.", "android.widget.", "androidx.fragment", "bottomnavigationview");
        var isNativeSource = source.Length == 0 || source is "native" or "ui";

        if (platform == VisualTreeContract.IosRuntimePlatform)
        {
            if (isNativeSource || hasUIKit)
            {
                AddUnique(results, iosUIKit);
            }
            if (hasSwiftUi)
            {
                AddUnique(results, iosSwiftUi);
            }
        }
        else if (platform == VisualTreeContract.MacCatalystRuntimePlatform)
        {
            if (isNativeSource || hasUIKit)
            {
                AddUnique(results, macCatalystUIKit);
            }
            if (hasSwiftUi)
            {
                AddUnique(results, macCatalystSwiftUi);
            }
        }
        else if (platform == VisualTreeContract.MacOsRuntimePlatform)
        {
            if (isNativeSource || hasAppKit)
            {
                AddUnique(results, macOsAppKit);
            }
            if (hasSwiftUi)
            {
                AddUnique(results, macOsSwiftUi);
            }
        }
        else if (platform == VisualTreeContract.AndroidRuntimePlatform)
        {
            if (isNativeSource || hasAndroidViews)
            {
                AddUnique(results, androidViews);
            }
            if (hasCompose)
            {
                AddUnique(results, androidCompose);
            }
        }
        else if (hasUIKit)
        {
            AddUnique(results, iosUIKit);
            if (hasSwiftUi)
            {
                AddUnique(results, iosSwiftUi);
            }
        }
        else if (hasAppKit)
        {
            AddUnique(results, macOsAppKit);
            if (hasSwiftUi)
            {
                AddUnique(results, macOsSwiftUi);
            }
        }
        else if (hasAndroidViews || hasCompose)
        {
            if (hasAndroidViews)
            {
                AddUnique(results, androidViews);
            }
            if (hasCompose)
            {
                AddUnique(results, androidCompose);
            }
        }
        else if (hasSwiftUi)
        {
            AddUnique(results, iosSwiftUi);
        }

        if (results.Count == 0)
        {
            results.Add(genericNative);
        }
        return results;
    }

    private static void AddUnique(
        ICollection<NavigationController> controllers,
        NavigationController controller)
    {
        if (!controllers.Any(candidate => string.Equals(
                candidate.Framework,
                controller.Framework,
                StringComparison.Ordinal)))
        {
            controllers.Add(controller);
        }
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(candidate => value.Contains(candidate, StringComparison.Ordinal));

    private static string? ReadString(JsonObject value, string propertyName)
        => value[propertyName] is JsonValue stringValue
           && stringValue.TryGetValue<string>(out var text)
            ? text
            : null;

    private static string Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
