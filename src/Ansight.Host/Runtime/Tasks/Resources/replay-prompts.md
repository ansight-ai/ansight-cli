<!-- template:task-description -->
Replay the workflow recorded between $START_UTC$ and $END_UTC$ and verify the UI settles.
<!-- /template -->

<!-- template:input-target-ancestor -->
the visible input field within the control with automation ID $AUTOMATION_ID$
<!-- /template -->

<!-- template:input-target-automation -->
the visible input field with automation ID $AUTOMATION_ID$
<!-- /template -->

<!-- template:input-target-label -->
the visible input field labelled $LABEL$
<!-- /template -->

<!-- template:instruction-automation-tap -->
Tap the visible control with automation ID $AUTOMATION_ID$.
<!-- /template -->

<!-- template:instruction-clear-input -->
Clear $INPUT_TARGET$.
<!-- /template -->

<!-- template:instruction-coordinate-tap -->
Replay the recorded tap exactly once by calling ansight_tap_ui with normalizedX=$NORMALIZED_X$ and normalizedY=$NORMALIZED_Y$. Do not substitute a selector, recenter, or repeat the tap.
<!-- /template -->

<!-- template:instruction-enter-input -->
Enter $INPUT_VALUE$ into $INPUT_TARGET$, replacing any existing text.
<!-- /template -->

<!-- template:instruction-swipe -->
Replay the recorded swipe exactly once by calling ansight_swipe_ui with startNormalizedX=$START_X$, startNormalizedY=$START_Y$, endNormalizedX=$END_X$, endNormalizedY=$END_Y$, and durationMs=$DURATION_MILLISECONDS$. Do not recenter, approximate, or repeat the gesture.
<!-- /template -->

<!-- template:instruction-text-tap -->
Tap the visible $TARGET_TYPE$ with text $TARGET_TEXT$.
<!-- /template -->
