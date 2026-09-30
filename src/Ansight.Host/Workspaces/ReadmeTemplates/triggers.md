# Triggers

A trigger is a small reaction to one app or session event. The host matches the
event first, then runs the TypeScript handler. For example: when an error log is
captured, request one diagnostic artifact from the same app session.

Read the full [Ansight workspace triggers guide](https://www.ansight.ai/docs/workspace/triggers).

Triggers are not schedules or background loops. Use a task when a person or
agent should explicitly start the work.

## Create one

```sh
ansight workspace add trigger . diagnostics.capture-errors \
  --app-id com.example.app \
  --event-kind session.log.received
```

A nested file such as `diagnostics/capture-errors.ts` has the trigger ID
`diagnostics.capture-errors`.

```ts
import type {
  TriggerContext,
  TriggerDefinition
} from "./ansight-trigger.d.ts";

export const trigger = {
  "schemaVersion": 1,
  "eventKind": "session.log.received",
  "conditions": [
    {
      "field": "payload.priority",
      "operator": "equals",
      "value": "Error"
    }
  ]
} satisfies TriggerDefinition;

export default async function captureError({ app }: TriggerContext) {
  return app.artifacts.request({
    providerId: "example.diagnostics",
    artifactId: "state",
    arguments: {}
  });
}
```

The descriptor must be a JSON-compatible object literal. Conditions are ANDed
and matched by the host before code runs. Keep them narrow: a broad match on a
high-frequency event can create a large queue of short-lived handlers.

A handler may return no action or one app-tool action. The host validates and
audits that action against the session and app permissions.

Check modules without executing them:

```sh
ansight repo automation inspect .
npx tsc -p ansight/triggers/tsconfig.json
```
