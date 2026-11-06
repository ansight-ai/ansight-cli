namespace Ansight.Host.AppGraphs;

internal static partial class AppGraphNavigationParadigms
{
    private static AppGraphNavigationParadigm Web(string kind) => kind switch
    {
        "web_router" => new("router_outlets", "Preserve exposed router outlets and nested routes; browser history is not the DOM child hierarchy.", "Record active route/outlet and URL when exposed.", "Distinguish browser history Back, router navigation and native presentation dismissal; observe which layer handles the action.", "Exposed router state, URL/history information and active DOM outlets; do not infer a specific web router from Capacitor alone."),
        "web_drawer" => Selection("DOM drawer destination choices and its content/outlet host.", "Expanded/open state, selected link and active router outlet.", "Closing the drawer does not imply browser history Back."),
        "web_tab_bar" => Selection("Observed tablist/tab/tabpanel relationships or exposed router-backed tab membership.", "aria-selected, aria-controls, active panel and router state where present.", "Local tab selection may leave the URL unchanged; route-backed tabs follow the observed router policy."),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Missing web navigation paradigm.")
    };
}
