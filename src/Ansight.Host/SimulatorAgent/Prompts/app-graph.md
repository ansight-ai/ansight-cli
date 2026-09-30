<!-- template:guidance -->
$GRAPH_PLANS$
Classify each candidate as full coverage, partial coverage, or no match before acting. Use at most one candidate; preserve explicitly requested UI paths and continue residual steps after partial coverage.
<!-- /template -->

<!-- template:action-frontier-complete -->
No queued action remains; finish incomplete scroll coverage or complete exploration.
<!-- /template -->

<!-- template:action-frontier-next -->
Next queued action: '$ACTION_ID$' on destination '$DESTINATION_ID$' using $TOOL_NAME$ with automationId '$AUTOMATION_ID$'.
<!-- /template -->

<!-- template:action-guard-attempted -->
App Graph action '$AUTOMATION_ID$' from destination '$DESTINATION_ID$' has already been attempted $ATTEMPT_COUNT$ times. Mark it blocked or unavailable with evidence instead of entering the same branch again. $FRONTIER_GUIDANCE$
<!-- /template -->

<!-- template:action-guard-terminal -->
App Graph action '$AUTOMATION_ID$' from destination '$DESTINATION_ID$' is already terminal with status '$STATUS$'. $FRONTIER_GUIDANCE$
<!-- /template -->

<!-- template:frontier-action -->
- $ACTION_ID$: destination=$DESTINATION_ID$, tool=$TOOL_NAME$, automationId=$AUTOMATION_ID$, status=$STATUS$, attempts=$ATTEMPT_COUNT$, meaning=$SEMANTIC_MEANING$
<!-- /template -->

<!-- template:frontier-empty -->
No persisted candidate exists yet. $NAVIGATION_HOST_COUNT$ navigation host(s) and $TAB_GROUP_COUNT$ tab group(s) are persisted. Observe the current destination and report its complete safe-action frontier before acting.
<!-- /template -->

<!-- template:frontier-pending -->
Resumed $DESTINATION_COUNT$ destination(s), $TRANSITION_COUNT$ transition(s), $NAVIGATION_HOST_COUNT$ navigation host(s), $TAB_GROUP_COUNT$ tab group(s), and $ACTION_COUNT$ action candidate(s). Explore these lowest-attempt pending actions first:
$PENDING_ACTIONS$
<!-- /template -->

<!-- template:frontier-terminal -->
The persisted frontier has $ACTION_COUNT$ terminal candidate(s), $NAVIGATION_HOST_COUNT$ navigation host(s), and $TAB_GROUP_COUNT$ tab group(s), with no pending action. Finish incomplete scroll or structure coverage, then complete exploration.
<!-- /template -->

<!-- template:guidance-truncated -->
[App Graph guidance truncated by the host. Treat omitted transitions as unavailable and fall back safely.]
<!-- /template -->

<!-- template:plan-binding-postconditions -->
       Postconditions: $POSTCONDITIONS$
<!-- /template -->

<!-- template:plan-binding-preconditions -->
       Preconditions: $PRECONDITIONS$
<!-- /template -->

<!-- template:plan-binding -->
     Binding $BINDING_ID$: priority=$PRIORITY$, mechanism=$MECHANISM$, confidence=$CONFIDENCE$, configuration=$CONFIGURATION$$PRECONDITIONS$$POSTCONDITIONS$
<!-- /template -->

<!-- template:plan-edge-postconditions -->
     Edge postconditions: $POSTCONDITIONS$
<!-- /template -->

<!-- template:plan-transition -->
  $INDEX$. [$EDGE_ID$] $FROM_STATE$ -> $TO_STATE$: $ACTION$$EDGE_POSTCONDITIONS$$BINDINGS$
<!-- /template -->

<!-- template:plan -->
- Published graph: $GRAPH_NAME$ ($GRAPH_ID$, version $VERSION_ID$)
  Declared intent: $INTENT$
  Planned target: $TARGET_STATE$$TRANSITIONS$
<!-- /template -->
