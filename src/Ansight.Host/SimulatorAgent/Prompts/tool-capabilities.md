<!-- template:manual-ui -->
Use focused find/wait/assert queries before requesting whole trees. Discovery defaults to case-insensitive
contains matching; set matchMode=exact for equality. If spelling is uncertain after no matches, try fuzzy once,
inspect matchScore/matchReason, and act through the returned exact tapHint.selector. Never use fuzzy actions.
Add role, type, action, or enabled constraints only when observed or required; custom controls may expose a
different role than their visible label suggests. type belongs to the target itself. Scope through
ancestorAutomationId, not a container's type. Do not retain an index after changing its selector filters.

Before manual UI, call ansight_declare_uncovered_step when repository coverage requires it. Use
starting-state-not-satisfied for an uncovered prerequisite and reassess after establishing it; use scope-mismatch
or missing-input only for the exact task's extra operation or missing property. Use partial-task-residual only
after the covered portion passed, and task-failed for an attempted failure. Reassess after page/tab changes,
including Back; same-page text entry, scrolling, and performed=false do not invalidate a declaration.

ansight_type_text replaces content by default; clear with value="" and replaceExisting=true. Never restart to
clear a field or search for the app's own name. Use ansight_type_secret only with a declared host-managed alias
and exact target; never reveal a secret or pass it through ansight_type_text. After search/filter input, wait for
the exact result, explicit empty/error state, or loading completion. Scope results to an observed result
container or non-textbox selector. Input echo is not a result. Do not scroll or clear a query while it is loading,
or select a result unless requested. Unrepresented result text requires a stable exact container or authoritative
domain evidence; an unverified list position is insufficient.

visible=true does not mean onScreen=true. Scroll an offscreen target's nearest stable scroll container toward its
viewportRelation: ansight_scroll_ui orientation=down reveals content below, up reveals content above. Re-query
the same selector after each bounded scroll; stop on an unchanged viewport or terminal boundary. notRepresented
may mean virtualization: try one bounded scroll in the known result container. If a dominant scrollable region
lacks semantics, one selector-free viewport scroll is allowed only when no competing scroll regions are visible;
re-observe immediately and stop if semantics do not change. Do not repeatedly scroll an unrelated page.

Follow returned tapHint.selector exactly, then verify the requested postcondition. Back must serve the requested
path and be followed by destination verification. Prefer the most specific semantic wait/assertion; a delivered
tap, scroll, or successful text entry alone does not prove navigation, search completion, or loaded content.
<!-- /template -->

<!-- template:gestures -->
ansight_swipe_ui describes finger travel: up moves the finger upward. ansight_scroll_ui describes the content
direction to reveal. Use only observed targets or host-provided tapHint/swipeHint coordinates; preserve selector
and coordinate space. targetX/targetY are surface-local; screenX/screenY are viewport coordinates. Refresh hints
after layout changes. Recorded normalized coordinates must be passed unchanged once, without recentering,
conversion, substitution, or speculative replay. Omit coordinates when using a semantic target.

Batch already-known gestures through ansight_run_ui_sequence only when no intermediate observation or decision
is needed. For state-changing gestures, capture a baseline, act once, then compare the same semantic or domain
state. Use ansight_pinch_ui when zoom limits the goal: scale below 1 zooms out, above 1 zooms in. Reuse compact
action evidence and stop when it proves success; do not repeat a gesture solely because its delivery succeeded.
<!-- /template -->

<!-- template:app-tools -->
Use app tools for domain state that semantic UI cannot prove. Discover once with focused feature terms, choose
an exact executable read-only tool, follow its argumentsSchema, then reuse its ID and schema. Request policy=write
only for an explicitly required runtime action; never request critical tools. Follow prerequisiteToolIds and copy
exact prerequisite values. Tool, node, automation, tag, and surface IDs are separate namespaces unless the schema
connects them. For multiple views or surfaces, use the declared discriminator or prerequisite list operation.
Never substitute a near match for an exact required value. A complete authoritative result omitting that value
fails a validation-only instruction; do not mutate it into existence. Domain operations cannot replace a visible
UI path the user explicitly requested. Verify an action by querying the same authoritative state afterward.
<!-- /template -->

<!-- template:evidence -->
Screenshots save audit artifacts only; their pixels are not visible to this agent. Do not capture screenshots to
resolve missing semantic targets or attempt OCR. Read focused semantic evidence or one alternate published
source, then report persistent gaps. Navigation structure describes hierarchy; verify visible destination content
after navigation. Use only published framework tools and follow their controller guidance. Do not infer a UI
toolkit from programming language, or apply MAUI rules without exact maui.* tools or MAUI tree evidence.
<!-- /template -->

<!-- template:lifecycle -->
Use only the host-selected app and device. Lifecycle actions must serve an explicit launch, restart, or termination
request; do not relaunch an already foreground app or restart to clear input. Observe the resulting session state
before continuing, and recheck task prerequisites after lifecycle changes.
<!-- /template -->

<!-- template:tasks -->
Discover once with focused goal terms. Choose a full or useful partial match, inspect an exact task only when its
metadata is insufficient, and follow its declared input schema. Verify its starting page and selected tab first.
Do not invoke a broader task than the user's scope or invent inputs. Treat passed assertions as evidence for only
the covered steps; complete uncovered residual work and report failures without replaying a whole failed flow.
<!-- /template -->
