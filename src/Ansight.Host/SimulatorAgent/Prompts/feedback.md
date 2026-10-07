<!-- template:app-graph-completion-rejected -->
The App Graph completion was rejected. Read the exact rejection returned by complete_instruction. If the host-owned action, scroll, or navigation-structure frontier remains incomplete, follow exact pending actions first and repair named host or tab inventory gaps from framework navigation and visual-tree evidence without revisiting terminal actions. If the completion JSON violates the App Graph exploration contract, repair the named destination, transition, navigation-host, or tab-group field. Call complete_instruction again only after the reported problem is corrected.
<!-- /template -->

<!-- template:app-graph-scroll-terminal -->
The App Graph scroll reached an unchanged semantic viewport. Treat this container and direction as terminal, report the scroll coverage change, and choose another safe branch. Do not scroll this scope again.
<!-- /template -->

<!-- template:app-tool-result -->
Evaluate the authoritative app-tool result against the current instruction. For a validation-only instruction, call complete_instruction immediately when the result is complete; omission of the exact required value is a failed assertion, and you must not mutate the UI to seek a different answer. For an action instruction, an unmet state is a precondition: use any exact returned action hint once, preserve its selector and coordinate space, then re-query the same state to verify the postcondition. Never act on a different entity or substitute an identifier from another namespace.
<!-- /template -->

<!-- template:camera-changed -->
Host camera comparison: cameraChanged=true. The authoritative post-swipe position or orientation differs from the pre-swipe baseline. Call complete_instruction now; do not find the player or swipe again.
<!-- /template -->

<!-- template:camera-unchanged -->
Host camera comparison: cameraChanged=false. The authoritative post-swipe camera still matches the baseline. Resolve the exact swipeHint target and retry the gesture at most once, then query once more.
<!-- /template -->

<!-- template:completion-grace -->
The $MAXIMUM_TURNS$-turn work budget is exhausted, but the final work turn produced a successful tool result. This is a completion-only pass: call $COMPLETION_TOOL$ exactly once with an honest succeeded or failed outcome based on the evidence already in history. Do not request or describe another action.
<!-- /template -->

<!-- template:continue-with-tool -->
Continue by calling an Ansight tool, or call complete_instruction. Do not answer only with text.
<!-- /template -->

<!-- template:enabled-filter-no-match -->
This focused text query found no match while requiring enabled=true. Text labels commonly do not publish an enabled state. Retry this exact focused query once with visible=true but omit enabled; if the single match includes tapHint, follow it immediately. Do not request the full tree or enumerate unrelated controls.
<!-- /template -->

<!-- template:query-not-represented -->
The exact query text was not represented by the current semantic tree. First check once for a loading/searching state and wait for it to end. If settled, do not repeat text searches or assume visual absence: inspect a focused result container or actionable descendant, or use an authorized graph, task, or app tool to establish the exact result. Do not tap an unverified list position.
<!-- /template -->

<!-- template:repeated-tool-failure -->
The exact same tool call has now failed twice. Do not repeat it; change the selector, arguments, or evidence source.
<!-- /template -->

<!-- template:retained-target-tap -->
Host retained one exact named target and its host-generated bounded tap arguments: $TAP_ARGUMENTS$. If the current instruction requires acting on that target and the layout has not changed, use these arguments once without substituting coordinates or another target, then verify the requested postcondition.
<!-- /template -->

<!-- template:scroll-delivered -->
The requested scroll gesture was delivered without an unexpected navigation postcondition failure. Re-observe with one focused query for the exact requested control using visible=true and omit enabled when searching by text. If a single result includes tapHint, act on it immediately; do not enumerate controls or request a full visual tree first.
<!-- /template -->

<!-- template:search-result-verified -->
The focused query verified the matching search result. The instruction does not ask to act on that result, so do not follow its tapHint. Complete after any remaining search-only verification.
<!-- /template -->

<!-- template:stagnation-warning -->
You have made $READ_ONLY_TOOL_CALLS$ consecutive read-only tool calls without acting or completing the instruction. The next call must perform the exact requested action or call complete_instruction; another read-only call will stop this instruction as a loop.
<!-- /template -->

<!-- template:swipe-delivered -->
The swipe was delivered. Query the same authoritative state once and compare it with the latest pre-swipe baseline already in history. If the requested state changed, complete immediately. Do not rediscover the target or swipe again before that comparison.
<!-- /template -->

<!-- template:tap-hint -->
The focused UI query returned one exact visible match with a host-generated tapHint. This instruction requires an action: call ansight_tap_ui now by lifting tapHint.selector into the tool arguments. Do not enumerate controls, request another full visual tree, add enabled=true, or guess alternate automation ids before acting. Then verify the requested postcondition.
<!-- /template -->

<!-- template:target-tap-delivered -->
The bounded target-local tap was delivered. Do not repeat it. Wait once for the most specific semantic postcondition named by the instruction, or re-query the same authoritative app state when the visual tree cannot represent that state. Complete only after the destination or state change is verified.
<!-- /template -->

<!-- template:task-failed -->
The repository task did not complete successfully. If it stopped before changing the app because the starting page or tab was missing, establish that prerequisite and retry the same task once after the state changes. Otherwise report the failure honestly; do not manually replay a failed action or assertion to hide it. Trust any assertions the task already passed.
<!-- /template -->

<!-- template:task-shortcut-assessment -->
Shortcut assessment: compare the returned task metadata with the complete current instruction now. Classify one exact candidate as full coverage, useful partial coverage, or no match. Run only a full or useful partial match with bindable required inputs. If a preloaded task is broader than the instruction or needs an unavailable input, declare scope-mismatch or missing-input once with the exact task ID and evidence. Do not invent input values or run a broader task. Stop task discovery when there is no exact candidate.
<!-- /template -->

<!-- template:text-entry-delivered -->
The requested text entry was delivered. If it triggers search or filtering, wait once for the exact result inside its observed results container, an explicit empty/error state, or the loading/searching state to end. The query echoed in the textbox is not a search result: do not use an unqualified text wait that matches the input. An immediate zero match while loading is not a completed search. Do not clear or scroll the results before the search settles. Select a result only when requested. Text entry alone does not require another task declaration.
<!-- /template -->
