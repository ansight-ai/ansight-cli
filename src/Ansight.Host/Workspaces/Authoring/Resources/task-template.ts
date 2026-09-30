import type { TaskDefinition, TaskInvocation } from "./ansight-task.d.ts";

export const task = {
  "schemaVersion": 1,
$APP_ID_PROPERTY$  "title": $TITLE$,
  "description": $DESCRIPTION$,
  "maximumActions": 24
} satisfies TaskDefinition;

export default async function runTask({ expect }: TaskInvocation) {
  expect(false, {
    id: "not-implemented",
    message: "Replace this placeholder with observable assertions."
  }).toBe(true);
}
