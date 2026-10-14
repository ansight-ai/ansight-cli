using System.Text.Json.Nodes;

namespace Ansight.Host.Runtime.Operations.Tools.UiAutomation;

/// <summary>Navigation evidence derived from complete snapshots, never from model arguments.</summary>
internal static class UiNavigationContext
{
    public static bool IsNavigationNode(LiveUiNodeMatch match)
    {
        var role = LiveUiNodeQuery.ReadRole(match.Node, match.TypeRegistry);
        var type = match.TypeRegistry.Resolve(match.Node);
        return HasNavigationSemantics(type, role)
               || LiveUiNodeQuery.ReadBoolean(match.Node, "currentPage", false, match.TypeRegistry)
               || LiveUiNodeQuery.ReadBoolean(match.Node, "activePage", false, match.TypeRegistry);
    }

    internal static bool HasNavigationSemantics(string? type, string? role)
        => role is "tab" or "tablist" or "dialog" or "alertdialog" or "navigation"
           || new[] { "Page", "TabBar", "TabLayout", "TabView", "BottomNavigation", "NavigationBar" }
               .Any(term => type?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);

    public static string? Fingerprint(JsonObject source)
    {
        if (source["truncated"]?.GetValue<bool>() == true
            || !VisualTreeTypeRegistry.TryCreateCompactV2(source, out var registry, out var root)
            || registry is null || root is null) return null;
        // A page-only capture cannot establish that a modal or persistent tab is unchanged.
        if (source["rootScope"]?.GetValue<string>() != "rootPage") return null;
        var nodes = LiveUiNodeQuery.Enumerate(root, registry).ToArray();
        var current = source["currentPage"] as JsonObject
                      ?? nodes.FirstOrDefault(match =>
                          LiveUiNodeQuery.ReadBoolean(match.Node, "currentPage", false, registry)
                          || LiveUiNodeQuery.ReadBoolean(match.Node, "activePage", false, registry))?.Node;
        if (current is null || Identity(current, registry) is not { } page) return null;
        var navigation = new List<string> { "page:" + page };
        foreach (var match in nodes.Where(match => LiveUiNodeQuery.IsEffectivelyVisible(match) && IsNavigationNode(match)))
        {
            var identity = Identity(match.Node, registry);
            if (identity is null) return null;
            navigation.Add(identity + ":selected=" + LiveUiNodeQuery.ReadBoolean(match.Node, "selected", false, registry));
        }
        return string.Join("\n", navigation.Order(StringComparer.Ordinal));
    }

    private static string? Identity(JsonObject node, VisualTreeTypeRegistry registry)
        => LiveUiNodeQuery.ReadAutomationId(node) ?? LiveUiNodeQuery.ReadString(node, "id");
}
