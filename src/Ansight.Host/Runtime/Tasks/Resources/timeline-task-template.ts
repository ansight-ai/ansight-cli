/**
 * Extracted from session $SESSION_ID_TEXT$.
 * Timeline: $START_UTC$ to $END_UTC$.
 * REVIEW: confirm the starting state and replace the final stability check with a product outcome assertion.
$DIAGNOSTIC_COMMENTS$ */
import type { TaskDefinition, TaskInvocation } from "./ansight-task.d.ts";

export const task = {
  "schemaVersion": 1,
  "appId": $APP_ID$,
  "title": $TITLE$,
  "description": $DESCRIPTION$,
  "feature": "recorded-workflow",
  "keywords": ["timeline", "recording", "replay"],
  "inputSchema": {
    "type": "object",
    "properties": {},
    "additionalProperties": false
  },
  "timeoutSeconds": 120,
  "maximumActions": $MAXIMUM_ACTIONS$
} satisfies TaskDefinition;

export default async function runTask({ ansight, expect }: TaskInvocation) {
$BODY$
  return { completedActions: $ACTION_COUNT$, sourceSessionId: $SESSION_ID$ };
}
