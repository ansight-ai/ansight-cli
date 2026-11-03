namespace Ansight.Host.AppGraphs;

internal static partial class AppGraphNavigationParadigms
{
    private static AppGraphNavigationParadigm Flutter(string kind) => kind switch
    {
        "navigator" or "nested_navigator" => Stack(
            "Preserve the owning Navigator's Route stack, including observed popup routes; do not combine separate nested Navigators.",
            "Navigator identity, exposed pages/Route history and current route. Widget ancestry alone does not reveal hidden routes."),
        "bottom_navigation_bar" or "navigation_rail" => Selection(
            "Navigation destinations are selection controls; each may own a separate Navigator.",
            "BottomNavigationBar.currentIndex or NavigationRail.selectedIndex and the app's exposed destinations.",
            "Back belongs to the selected Navigator or app policy; selecting a destination is not a push."),
        "tab_bar" => Selection(
            "TabBar and TabBarView membership is coordinated by the same TabController.",
            "TabController.length/index and the linked TabBar/TabBarView; do not turn animation offsets into nodes.",
            "Tab selection is distinct from Navigator.pop; record any app-specific back policy from observation."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Missing Flutter navigation paradigm.")
    };
}
