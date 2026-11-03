namespace Ansight.Host.AppGraphs;

internal static partial class AppGraphNavigationParadigms
{
    private static AppGraphNavigationParadigm ReactNative(string kind) => kind switch
    {
        "react_navigation_stack" or "react_navigation_native_stack" => Stack(
            "Preserve router routes, route keys and nested state; index is authoritative, so do not assume the final array entry is focused.",
            "React Navigation state.type, routes[].key, routes[].state, index and available history."),
        "react_navigation_drawer" => Selection(
            "Drawer router routes are destination branches; drawer open/closed state is separate from the focused route.",
            "Drawer router routes, index, nested state and history/configuration when exposed.",
            "Back may close the drawer or follow configured backBehavior/history; do not assume pop or first-route return."),
        "react_navigation_bottom_tabs" or "react_navigation_material_top_tabs" => Selection(
            "Tab router routes in reported order with stable route keys and independent nested states.",
            "Tab router routes, index, nested state and exposed history/backBehavior.",
            "Switch tabs without flattening their stacks. Back behavior depends on router configuration and observed history."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Missing React Navigation paradigm.")
    };
}
