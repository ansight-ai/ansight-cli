<!-- template:manual-ui -->
Use focused find/wait queries and exact observed selectors or tapHint.selector.
Add role, type, action, or enabled constraints only when observed or required.
Selector fields are ANDed on one node; type belongs to the target itself, not its ancestor.
Discovery supports contains/exact/fuzzy; actions use exact hints. Keep
index and targetFingerprint unchanged. Short node IDs expire with the next observation or UI action.

An accepted uncovered-step declaration loads manual-ui automatically. Reassess after a confirmed page/tab/modal
transition or unknown navigation state. Unchanged navigation, typing and rejected actions retain the assessment.
Use taskDecisions to exclude several irrelevant tasks with individual evidence in one declaration.

Run 1–8 already-planned find/tap/type/wait calls through ansight_run_ui_batch. Use usePreviousTarget=true on
tap/type immediately after a unique find, with only action options; the host copies its exact target.
Never guess future targets, put tasks/completion in a UI batch, or replay a successful prefix after a later failure.
Type focuses and replaces the field by default; no separate tap is needed. Search input echo is not a result:
wait for the exact result outside the input, an explicit empty/error state, or loading completion.

Load extended-ui for scroll/assert/secret entry; gestures for swipe/pinch. Visible does not imply on-screen.
Use observed targets and verify the requested postcondition. Back must serve the requested path.
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
For a confirmed MAUI app, prefer its published native visual tree and device accessibility for visible UI.
Custom sheets and dialogs can be separate native windows absent from MAUI currentPage, rootPage, and window
captures. Use MAUI trees for framework state and bindings. Keep native sheet evidence authoritative, scope
queries to that sheet, and do not interact with its background page. Truncation is not proof of absence.
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
