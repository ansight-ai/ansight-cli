using System.Text.Json.Nodes;
using Ansight.Host.SimulatorAgent;

namespace Ansight.Host.AppGraphs;

public sealed record AppGraphNavigationTechnologyDescriptor(
    string Framework,
    string Kind,
    string NavigationToolId,
    string StructureFingerprint);

public sealed record AppGraphNavigationTechnologyKind(
    string Kind,
    string Label,
    string NormalizedRole,
    bool SupportsNavigationHost,
    bool SupportsTabGroup);

public sealed record AppGraphNavigationFramework(
    string Framework,
    string Label,
    IReadOnlyList<AppGraphNavigationTechnologyKind> Kinds);

public static class AppGraphNavigationTechnologyCatalog
{
    public const string NavigationHostScope = "navigation_host";
    public const string TabGroupScope = "tab_group";

    private static readonly IReadOnlyList<AppGraphNavigationFramework> frameworks =
    [
        Framework("maui", ".NET MAUI",
            Kind("shell_flyout", "Shell Flyout", "flyout", host: true),
            Kind("shell_tab_bar", "Shell TabBar", "bottom_tabs", host: true, tabs: true),
            Kind("shell_section", "Shell Section", "shell", host: true),
            Kind("navigation_page", "NavigationPage", "shell", host: true),
            Kind("tabbed_page", "TabbedPage", "top_tabs", host: true, tabs: true),
            Kind("modal_stack", "Modal stack", "other", host: true)),
        Framework("react-native", "React Native",
            Kind("react_navigation_drawer", "React Navigation Drawer", "drawer", host: true),
            Kind("react_navigation_bottom_tabs", "React Navigation Bottom Tabs", "bottom_tabs", host: true, tabs: true),
            Kind("react_navigation_material_top_tabs", "React Navigation Material Top Tabs", "top_tabs", host: true, tabs: true),
            Kind("react_navigation_stack", "React Navigation Stack", "shell", host: true),
            Kind("react_navigation_native_stack", "React Navigation Native Stack", "shell", host: true)),
        Framework("flutter", "Flutter",
            Kind("navigator", "Navigator", "shell", host: true),
            Kind("nested_navigator", "Nested Navigator", "shell", host: true),
            Kind("bottom_navigation_bar", "BottomNavigationBar", "bottom_tabs", host: true, tabs: true),
            Kind("navigation_rail", "NavigationRail", "navigation_rail", host: true),
            Kind("tab_bar", "TabBar / TabController", "top_tabs", host: true, tabs: true)),
        Framework("capacitor", "Capacitor",
            Kind("web_router", "Web Router", "shell", host: true),
            Kind("web_drawer", "Web Drawer", "drawer", host: true),
            Kind("web_tab_bar", "Web Tab Bar", "bottom_tabs", host: true, tabs: true)),
        Framework("ios-uikit", "iOS UIKit",
            Kind("ui_navigation_controller", "UINavigationController", "shell", host: true),
            Kind("ui_tab_bar_controller", "UITabBarController", "bottom_tabs", host: true, tabs: true),
            Kind("ui_split_view_controller", "UISplitViewController", "navigation_rail", host: true),
            Kind("presented_view_controller", "Presented UIViewController", "other", host: true)),
        Framework("ios-swiftui", "iOS SwiftUI",
            Kind("navigation_stack", "NavigationStack", "shell", host: true),
            Kind("navigation_split_view", "NavigationSplitView", "navigation_rail", host: true),
            Kind("tab_view", "TabView", "bottom_tabs", host: true, tabs: true),
            Kind("sheet", "Sheet", "other", host: true),
            Kind("full_screen_cover", "Full-screen cover", "other", host: true)),
        Framework("maccatalyst-uikit", "Mac Catalyst UIKit",
            Kind("ui_navigation_controller", "UINavigationController", "shell", host: true),
            Kind("ui_tab_bar_controller", "UITabBarController", "bottom_tabs", host: true, tabs: true),
            Kind("ui_split_view_controller", "UISplitViewController", "navigation_rail", host: true),
            Kind("presented_view_controller", "Presented UIViewController", "other", host: true)),
        Framework("maccatalyst-swiftui", "Mac Catalyst SwiftUI",
            Kind("navigation_stack", "NavigationStack", "shell", host: true),
            Kind("navigation_split_view", "NavigationSplitView", "navigation_rail", host: true),
            Kind("tab_view", "TabView", "bottom_tabs", host: true, tabs: true),
            Kind("sheet", "Sheet", "other", host: true)),
        Framework("macos-appkit", "macOS AppKit",
            Kind("ns_page_controller", "NSPageController", "shell", host: true),
            Kind("ns_split_view_controller", "NSSplitViewController", "navigation_rail", host: true),
            Kind("ns_tab_view_controller", "NSTabViewController", "top_tabs", host: true, tabs: true),
            Kind("sheet", "Sheet", "other", host: true),
            Kind("popover", "Popover", "other", host: true)),
        Framework("macos-swiftui", "macOS SwiftUI",
            Kind("navigation_stack", "NavigationStack", "shell", host: true),
            Kind("navigation_split_view", "NavigationSplitView", "navigation_rail", host: true),
            Kind("tab_view", "TabView", "top_tabs", host: true, tabs: true),
            Kind("window_group", "WindowGroup", "other", host: true),
            Kind("sheet", "Sheet", "other", host: true)),
        Framework("android-views", "Android Views",
            Kind("drawer_layout", "DrawerLayout", "drawer", host: true),
            Kind("bottom_navigation_view", "BottomNavigationView", "bottom_tabs", host: true, tabs: true),
            Kind("navigation_rail_view", "NavigationRailView", "navigation_rail", host: true),
            Kind("nav_controller", "NavController", "shell", host: true),
            Kind("tab_layout_view_pager", "TabLayout / ViewPager", "top_tabs", host: true, tabs: true)),
        Framework("android-compose", "Jetpack Compose",
            Kind("nav_host", "NavHost", "shell", host: true),
            Kind("modal_navigation_drawer", "ModalNavigationDrawer", "drawer", host: true),
            Kind("navigation_bar", "NavigationBar", "bottom_tabs", host: true, tabs: true),
            Kind("navigation_rail", "NavigationRail", "navigation_rail", host: true),
            Kind("tab_row", "TabRow", "top_tabs", host: true, tabs: true),
            Kind("pager", "Pager", "top_tabs", host: true, tabs: true)),
        Framework("native-unknown", "Unknown native framework",
            Kind("generic_host", "Generic navigation host", "other", host: true),
            Kind("generic_tabs", "Generic tab group", "top_tabs", tabs: true))
    ];

    public static IReadOnlyList<AppGraphNavigationFramework> Frameworks => frameworks;

    public static IReadOnlyList<AppGraphNavigationTechnologyKind> GetKinds(
        string? framework,
        string? scope = null)
    {
        var match = frameworks.FirstOrDefault(candidate => string.Equals(
            candidate.Framework,
            framework?.Trim(),
            StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return [];
        }

        return scope switch
        {
            NavigationHostScope => match.Kinds.Where(static kind => kind.SupportsNavigationHost).ToArray(),
            TabGroupScope => match.Kinds.Where(static kind => kind.SupportsTabGroup).ToArray(),
            _ => match.Kinds
        };
    }

    public static JsonArray BuildKindsJson(string? framework)
        => new(GetKinds(framework).Select(kind => (JsonNode?)new JsonObject
        {
            ["kind"] = kind.Kind,
            ["label"] = kind.Label,
            ["normalizedRole"] = kind.NormalizedRole,
            ["supportsNavigationHost"] = kind.SupportsNavigationHost,
            ["supportsTabGroup"] = kind.SupportsTabGroup
        }).ToArray());

    public static bool TryValidate(
        AppGraphNavigationTechnologyDescriptor? technology,
        string scope,
        out string normalizedRole,
        out string error)
    {
        normalizedRole = string.Empty;
        if (technology is null
            || string.IsNullOrWhiteSpace(technology.Framework)
            || string.IsNullOrWhiteSpace(technology.Kind))
        {
            error = "A framework-specific navigation technology is required.";
            return false;
        }

        var framework = frameworks.FirstOrDefault(candidate => string.Equals(
            candidate.Framework,
            technology.Framework.Trim(),
            StringComparison.OrdinalIgnoreCase));
        var kind = framework?.Kinds.FirstOrDefault(candidate => string.Equals(
            candidate.Kind,
            technology.Kind.Trim(),
            StringComparison.OrdinalIgnoreCase));
        var supportsScope = scope switch
        {
            NavigationHostScope => kind?.SupportsNavigationHost == true,
            TabGroupScope => kind?.SupportsTabGroup == true,
            _ => false
        };
        if (framework is null || kind is null || !supportsScope)
        {
            error = $"Navigation technology '{technology.Framework}/{technology.Kind}' is not supported for {scope.Replace('_', ' ')}s.";
            return false;
        }

        var controller = NavigationControllerCatalog.ResolveFramework(framework.Framework);
        var navigationToolId = technology.NavigationToolId?.Trim() ?? string.Empty;
        if (navigationToolId.Length > 0
            && (controller is null
                || !controller.NavigationToolIds.Contains(navigationToolId, StringComparer.Ordinal)))
        {
            error = $"Navigation tool '{navigationToolId}' does not belong to framework '{framework.Framework}'.";
            return false;
        }
        var fingerprint = technology.StructureFingerprint?.Trim() ?? string.Empty;
        if (navigationToolId.Length > 0
            && (fingerprint.Length != 64 || fingerprint.Any(static character => !Uri.IsHexDigit(character))))
        {
            error = "Navigation-controller evidence requires a SHA-256 structure fingerprint.";
            return false;
        }

        normalizedRole = kind.NormalizedRole;
        error = string.Empty;
        return true;
    }

    public static bool TryReadAndValidate(
        JsonObject? value,
        string scope,
        out AppGraphNavigationTechnologyDescriptor? technology,
        out string normalizedRole,
        out string error)
    {
        technology = value is null
            ? null
            : new AppGraphNavigationTechnologyDescriptor(
                ReadString(value, "framework") ?? string.Empty,
                ReadString(value, "kind") ?? string.Empty,
                ReadString(value, "navigationToolId") ?? string.Empty,
                ReadString(value, "structureFingerprint") ?? string.Empty);
        return TryValidate(technology, scope, out normalizedRole, out error);
    }

    public static JsonObject ToJson(AppGraphNavigationTechnologyDescriptor technology)
        => new()
        {
            ["framework"] = technology.Framework.Trim().ToLowerInvariant(),
            ["kind"] = technology.Kind.Trim().ToLowerInvariant(),
            ["navigationToolId"] = technology.NavigationToolId?.Trim() ?? string.Empty,
            ["structureFingerprint"] = technology.StructureFingerprint?.Trim().ToLowerInvariant() ?? string.Empty
        };

    private static AppGraphNavigationFramework Framework(
        string framework,
        string label,
        params AppGraphNavigationTechnologyKind[] kinds)
        => new(framework, label, kinds);

    private static AppGraphNavigationTechnologyKind Kind(
        string kind,
        string label,
        string normalizedRole,
        bool host = false,
        bool tabs = false)
        => new(kind, label, normalizedRole, host, tabs);

    private static string? ReadString(JsonObject source, string propertyName)
        => source[propertyName] is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;
}
