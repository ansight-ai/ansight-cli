namespace Ansight.Host.AppGraphs;

internal static partial class AppGraphNavigationParadigms
{
    private static AppGraphNavigationParadigm AndroidViews(string kind) => kind switch
    {
        "nav_controller" => Stack("NavController's rendered destination back stack; graph entries are structural owners, not extra visible screens.", "Exposed NavController graph, currentDestination, back-stack entries and nested graph ownership."),
        "drawer_layout" => Selection("DrawerLayout separates drawer content from main content; menu destinations require observed navigation evidence.", "Drawer open state, menu selection and the main content's FragmentManager/NavController.", "Back may close the drawer before reaching the active navigation stack; verify the observed result."),
        "bottom_navigation_view" or "navigation_rail_view" => Selection("Menu destinations and their linked content/navigation hosts.", "Menu item IDs, selectedItemId and linked NavController state.", "Selection may restore independent back stacks. Do not infer a push or reset without evidence."),
        "tab_layout_view_pager" => Selection("TabLayout tabs and linked ViewPager/ViewPager2 pages in adapter order.", "TabLayout selected tab, pager currentItem and observed adapter/page membership.", "Page selection is independent of the Activity/Fragment navigation back stack."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Missing Android Views navigation paradigm.")
    };

    private static AppGraphNavigationParadigm AndroidCompose(string kind) => kind switch
    {
        "nav_host" => Stack("NavHost destinations owned by its NavController; retain nested graph boundaries separately from rendered entries.", "App-exposed NavController current destination and back stack; recomposed semantics nodes are not routes."),
        "modal_navigation_drawer" => Selection("Drawer content and its observed destination choices remain separate from the main navigation host.", "DrawerState, selected destination and exposed NavController state.", "Close the drawer independently from popping the active NavController."),
        "navigation_bar" or "navigation_rail" => Selection("Declared navigation items and their app-owned selection/navigation state.", "Item selected state and app-exposed routing; the composable alone does not define navigation history.", "Observe the app's multiple-back-stack/restore policy; selection alone does not imply a push."),
        "tab_row" or "pager" => Selection("Declared tabs/pages in reported order; preserve app-defined linkage between TabRow and pager.", "selectedTabIndex/currentPage and observed page membership; ignore transient scroll offsets and recomposition.", "Tab/page selection is distinct from NavController.popBackStack; app policy controls Back."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Missing Compose navigation paradigm.")
    };
}
