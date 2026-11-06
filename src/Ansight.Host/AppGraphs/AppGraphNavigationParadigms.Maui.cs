namespace Ansight.Host.AppGraphs;

internal static partial class AppGraphNavigationParadigms
{
    private static AppGraphNavigationParadigm Maui(string kind) => kind switch
    {
        "flyout_page" => new(
            "drawer_content", "FlyoutPage.Detail is the content host; Flyout is the menu surface. Preserve any nested TabbedPage or NavigationPage under Detail.",
            "IsPresented controls the menu; it does not select a tab or push a page.",
            "Close the flyout independently of popping Detail's NavigationPage or dismissing a modal.",
            "FlyoutPage.Flyout, Detail, IsPresented and the direct page hierarchy."),
        "shell_flyout" => new(
            "hierarchical_routes", "Preserve ShellItem -> ShellSection -> ShellContent ownership; flyout menu commands are actions, not extra content children.",
            "Record CurrentItem at each Shell level and FlyoutIsPresented separately.",
            "Observe route-stack pops separately from Shell item selection and flyout closure.",
            "Shell.Items, nested Items, CurrentItem, CurrentState and observed route/navigation stacks."),
        "shell_section" => new(
            "hierarchical_routes", "ShellSection owns ShellContent choices plus its navigation state; do not flatten pushed pages into sibling content choices.",
            "CurrentItem selects ShellContent; the section's navigation stack identifies pushed content.",
            "A pop within the selected section differs from selecting another section.",
            "ShellSection.Items, CurrentItem and Navigation.NavigationStack."),
        "shell_tab_bar" or "tabbed_page" => Selection(
            kind == "tabbed_page" ? "TabbedPage.Children in reported order." : "Shell TabBar's direct ShellSection children in reported order.",
            kind == "tabbed_page" ? "TabbedPage.Children and CurrentPage." : "Shell TabBar.Items and CurrentItem.",
            "Tab selection preserves nested ownership; observe the selected child's back stack independently."),
        "navigation_page" => Stack("NavigationPage.NavigationStack from root to current page.", "Navigation.NavigationStack and CurrentPage.", true),
        "modal_stack" => new(
            "presentation_stack", "Navigation.ModalStack in presentation order, preserving each modal's internal hosts.",
            "The top modal is active; underlying presenters remain return context.",
            "PopModal/dismiss returns to the presenter; an inner NavigationPage pop is a separate action.",
            "Navigation.ModalStack and native foreground presentation evidence.", true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Missing MAUI navigation paradigm.")
    };
}
