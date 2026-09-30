<!-- template:app-graph-route -->
Before exploratory work on a multi-step instruction, perform one bounded shortcut assessment. Authorized App Graph
routes are available for this run. Prefer the shortest exact route whose states, transitions, bindings, and
postconditions cover the workflow. A route may cover only a prefix; execute it once and continue only the residual
steps. Reject mismatched or unresolved routes. Graph guidance never authorizes invented tools, selectors, URL
actions, secrets, or non-UI mutation when the instruction explicitly requires a visible UI path.
<!-- /template -->

<!-- template:app-graph-teaching -->
When report_app_graph_progress is available during exhaustive App Graph teaching, call it after the first verified
destination and after each newly verified destination, transition, or scroll-coverage change. Report only evidence
already established with Ansight tools. For each current destination, report every observed safe action as a stable
action candidate and give it queued, attempted, explored, blocked, unsafe, or unavailable status. Reuse the exact
candidate id on later reports. The host owns the cumulative frontier and returns the next concrete pending actions;
follow that frontier instead of revisiting a terminal action. Classify every destination's scrollStatus: unknown and
in_progress keep the host frontier open, while not_scrollable, complete, and blocked are terminal classifications.
At the start of App Graph teaching, call ansight_get_live_navigation_structure once. If the app exposes framework
navigation evidence, follow its controller guidance. If availableNavigationControllers contains another toolkit,
call the tool once for each additional framework override before acting so mixed navigation hierarchies are retained.
Report persistent flyout, drawer, tab-bar, rail, or Shell roots as navigationHosts. If it reports that no toolkit
navigation tool is available, request one app tree with ansight_get_live_visual_tree and
toolId=ui.get_visual_tree; device accessibility alone does not identify the toolkit. Report internal tab sets as
tabGroups before changing tabs: the currently selected tab destination is already observed even though no transition
was needed to reach it. Inventory every ordered host child and tab destination from structural evidence, then use
the visual tree to verify visible content and stable actions. For native trees, use every returned
graphStructureController: UIKit, SwiftUI, AppKit, Android Views, and Compose may coexist in one app. Never infer a UI
toolkit from Swift, Kotlin, Java, or .NET alone, and never apply MAUI rules unless exact maui.* tools or MAUI tree
evidence are present. Progress reporting does not replace observation and is not completion.
<!-- /template -->
