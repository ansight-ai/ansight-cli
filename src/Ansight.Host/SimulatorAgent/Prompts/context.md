<!-- template:instruction-input -->
Instruction $INSTRUCTION_NUMBER$/$INSTRUCTION_COUNT$:
$INSTRUCTION$

Session:
$SESSION_CONTEXT$

$PREVIOUS_RESULTS$

$REPOSITORY_TASKS_SECTION$

$KNOWN_APP_TOOL_CALLS$

$APP_GRAPH_GUIDANCE_SECTION$
$APP_GRAPH_FRONTIER_SECTION$
<!-- /template -->

<!-- template:repository-task -->
Function `$TOOL_NAME$`; repository task ID `$TASK_ID$`.
<!-- /template -->

<!-- template:repository-tasks-section -->
Preloaded tasks:
$TASKS$
<!-- /template -->

<!-- template:app-graph-guidance-section -->
Authorized App Graph routes:
$APP_GRAPH_GUIDANCE$
<!-- /template -->

<!-- template:app-graph-frontier-section -->
App Graph frontier:
$APP_GRAPH_FRONTIER$
<!-- /template -->

<!-- template:device-unavailable -->
Unknown
<!-- /template -->

<!-- template:known-app-tool-call -->
- $TOOL_ID$ with arguments $ARGUMENTS$
<!-- /template -->

<!-- template:known-app-tool-calls -->
$TOOL_CALLS$
These app-tool calls succeeded earlier; reuse their exact IDs and argument shapes when relevant.
<!-- /template -->

<!-- template:known-app-tool-calls-none -->
<!-- /template -->

<!-- template:previous-result -->
Earlier instruction $INDEX$ [$STATUS$]: $SUMMARY$
<!-- /template -->

<!-- template:previous-results-none -->
<!-- /template -->

<!-- template:session-unavailable -->
Selected session lifecycle unavailable; observe first.
<!-- /template -->

<!-- template:session-context -->
App: $APP_NAME$ ($APP_ID$)
Session: $SESSION_ID$
Device: $DEVICE$
Live: $IS_LIVE$
Lifecycle: $APP_STATE$
$LAUNCH_STATE$
<!-- /template -->

<!-- template:session-launch-live -->
Already launched, connected, and foreground.
<!-- /template -->

<!-- template:session-launch-unverified -->
Foreground state unverified; observe first.
<!-- /template -->
