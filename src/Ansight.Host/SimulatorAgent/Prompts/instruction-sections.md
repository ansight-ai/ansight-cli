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
already established with Ansight tools. Report every observed action, including actions without stable automation IDs.
Preserve their exact observed selector; missing automation IDs alone are not grounds for blocked/unavailable status.
Use queued, attempted, needs_verification, deferred, explored, blocked, unsafe, unavailable, or navigation status. Back/pop/dismiss controls, tab switches, and menu toggles are
reusable navigation essentials: set navigationKind and navigation status, never explored. Reuse the exact
candidate id on later reports. Traverse verified safe edges again to reach unfinished branches without resetting
explored actions to queued. A verified same-screen state change permits one bounded follow-up to restore
transient state; no_change does not grant this exception. Recheck blocked branches only with new evidence or a repaired capability; unsafe actions stay excluded. The host owns the cumulative frontier and returns the next concrete pending actions;
follow that frontier, using reusable navigation essentials to return to its branch points. Classify every destination's scrollStatus: unknown and
in_progress keep coverage open. Only not_scrollable and complete count as covered; blocked remains an unresolved gap.
On resume, reconcile the retained canonical inventory before opening new branches. Unknown state IDs are rejected;
every referenced destination must already exist or be included in the same report.
Coverage-only updates retain the actual currentDestinationId even when updating another node. To correct a
known current state without input, inspect the full native/accessibility hierarchy (ui.get_visual_tree with root
window), or assess a fresh full screenshot, and bind stateObservation to that host-issued observationId with
foreground evidence. Focused find/wait/assert matches and OCR excerpts can describe content behind a sheet;
they cannot establish that the covering sheet is absent and do not issue state-correction observation IDs.
Accepted reports consume that observation; do not reuse it for another jump.
Use transitionCorrections only to repair evidence-backed source/destination attribution errors in retained edges;
send each correction once and clear it after acceptance. Retire any wrongly attributed action alias separately
while preserving its correct canonical action. A known screen-entry transition can restore a contained state:
keep the exact observed child as currentDestinationId; the host preserves the proven route into its owning screen.
Rejected reports apply no changes:
correct the error and resubmit the intended update. A gap description does not change an action or scroll status.
Mark explicitly excluded actions unsafe and obsolete failed selector aliases unavailable with evidence; retain the
canonical successful action. After EVERY delivered input, report currentDestinationId AND actionOutcome bound to the host-provided inputId,
actionId and evidenceRefs. Set actionOutcome.navigationKind for Back/pop/dismiss, tabs and menu toggles,
even when the action was not inventoried before input; closing a screen or sheet is dismiss. Observe the after-state before reporting verified or no_change. A successful input
alone proves delivery, not the action's intended outcome. If the resulting page is unrelated to the intended
action, report needs_verification, investigate targeting with read-only tools, then defer or block that action
with a concrete reason. needs_verification keeps the input gate closed and may leave currentDestinationId empty when the
foreground state is still unknown; do not guess an ID to satisfy a report. deferred permits other work while
retaining this candidate; blocked requires an observed observation, target, capability or application limitation.
Before declaring a non-text effect or foreground state unobservable, load evidence, capture a fresh screenshot,
and use ansight_evaluate_screenshot with concrete visible criteria and exact retained frame IDs. Reference the
assessment in the outcome evidence alongside the original input evidence. Inconclusive assessments keep work
unresolved; repeated unchanged tree/OCR queries do not substitute for visual assessment.
A run budget or focus limit is deferred work, never a blocker or completed coverage. Navigation essentials stay reusable.
The host derives explored coverage from resolved action outcomes, not from tool success or gap descriptions.
Use complete_instruction outcome partial when a pass ends with unresolved work; succeeded requires full safe
coverage. Retained frontier and pending evidence survive a partial pass or restart.
Reuse canonical destination IDs. Distinct same-named states require identityEvidence; use destinationAliases
with observed equivalence evidence to merge accidental duplicate states and redirect their graph references.
Retired aliases keep their observed selectors and stable automation IDs. If a reusable navigation action was
incorrectly retired, revalidate it from a fresh observation and update that same canonical ID to navigation with
the observed evidence in lastOutcome; do not invent another alias. Explicit unsafe exclusions remain terminal.
Finish or explicitly classify the current branch before navigating away. Native sheet lists may be flattened out
of accessibility: use the native window tree and a fresh scroll selector/provider hint, then verify viewport change.
At the start of App Graph teaching, call ansight_get_live_navigation_structure once. If the app exposes framework
navigation evidence, follow its controller guidance. If availableNavigationControllers contains another toolkit,
call the tool once for each additional framework override before acting so mixed navigation hierarchies are retained.
Report persistent flyout, drawer, tab-bar, or rail roots as navigationHosts. Preserve direct ownership:
Flyout -> Tabbed page -> Tab screens. A nested tab host is a child destination of its flyout; its individual
screens are children of the tab host, not also direct children of the flyout. One observed child is valid for
flyout and drawer hosts. Reuse container IDs, and never invent action transitions for ownership.
Use navigationHosts for full-page tabs and tabGroups for named states within a screen. If it reports that no toolkit
navigation tool is available, request one app tree with ansight_get_live_visual_tree and
toolId=ui.get_visual_tree; device accessibility alone does not identify the toolkit. Report internal tab sets as
tabGroups before changing tabs: the currently selected tab destination is already observed even though no transition
was needed to reach it. Inventory every ordered host child and tab destination from structural evidence, then use
the visual tree to verify visible content and stable actions. Use every returned graphStructureController:
multiple UI toolkits may coexist in one app. Apply only the framework guidance composed from published tools
or observed controllers; never infer a toolkit from implementation language alone. Progress reporting does not
replace observation and is not completion.
<!-- /template -->
