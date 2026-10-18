# Tasks

A task is a named recipe that runs against one live app session. Think of it as
teaching Ansight a repeatable job once—such as opening a screen, reading app
state, and checking the result—then invoking that exact job whenever you need it.

Read the full [Ansight workspace tasks guide](https://www.ansight.ai/docs/workspace/tasks).

Use a task when the steps should be deterministic and explicitly started. Use a
test when an agent should reason through a user journey, or a trigger when work
should begin automatically after an event.

## Create one

```sh
ansight workspace add task . home.verify \
  --app-id com.example.app \
  --title "Verify signed-in home"
```

The command creates `home.verify.ts`. A nested path such as
`map/validate-areas.ts` has the task ID `map.validate-areas`.

Every module exports a static descriptor and a default function:

```ts
import type { TaskDefinition, TaskInvocation } from "./ansight-task.d.ts";

export const task = {
  "schemaVersion": 1,
  "title": "Verify signed-in home",
  "description": "Checks that the signed-in home is visible.",
  "platforms": ["ios", "android"],
  "deviceKinds": ["virtual", "physical"],
  "frameworks": ["dotnet-maui"],
  "maximumActions": 4
} satisfies TaskDefinition;

export default async function verifyHome({ ansight, expect }: TaskInvocation) {
  const result = await ansight.ui.assert({
    automationId: "signed-in-home",
    exists: true,
    expectedVisible: true
  });
  expect(result.passed, {
    id: "signed-in-home-visible"
  }).toBe(true);
}
```

The descriptor must remain a JSON-compatible object literal because Ansight
reads it without executing the module. Use `schemaVersion: 1`, or omit it.
Optional `requires.capabilities` and `requires.appTools` are checked before
importing the task in both SDK and external device sessions. Individual tool
calls still enforce provider availability.

A normal return with no named `expect`
assertion is **inconclusive**, not passed.

## Describe task transitions

Optional `startingState` and `successState` strings help the agent plan a path and
chain tasks. Each accepts 1–1000 characters:

```ts
"startingState": "Map tab is open with its search field available.",
"successState": "Map tab remains open; the area named by input.searchArea is selected in both the map and card."
```

Describe relevant pages, selected tabs, selections and state preserved by the task.
Use input names in prose for parameter-dependent state. These are planning hints:
the task still checks its prerequisites and proves its claimed outcomes with
checks and named assertions. Hints alone never prove a live state or task success.
Omitting them preserves existing task behavior; `outputSchema` still describes
returned data, not app state.

The agent can run 1–8 already-planned tasks with `ansight_run_task_batch`, supplying
`reason` and `steps: [{ taskId, input }]`. Its user-visible reason explains why the
tasks fit together and need no intermediate decision. The host runs them in order,
retains each task's guards, limits, assertions and trace, and stops on the first
failure. Later steps are reported as skipped. A step may set `cooldownMs` from 0 to
10000 to let the UI settle before the next task. It defaults to zero, is cancellable,
and applies only after success when another task follows. Explain chosen waits in
`reason`; requested and elapsed wait times are retained in the batch trace. A delay
does not replace readiness checks or assertions. Inputs must all be known upfront;
there is no parallel execution or output-reference substitution. Review results
before completion and do not replay a successful prefix. Use task composition for
reusable authored workflows; use agent batching for a sequence assembled for the
current instruction.

## Tell the test runner what completed

Use named assertions with messages that explain the verified outcome. The runtime
reports `Passed` when the task finishes with passing assertions; a returned
`success: true` flag does not replace them. Return a short `summary` and any useful
measured values so the agent can understand the completed scope:

```ts
expect(result.passed, {
  id: "signed-in-home-visible",
  message: "The signed-in home screen is visible."
}).toBe(true);
return { summary: "Opened and verified the signed-in home screen." };
```

The return value reaches the agent as `result.output`. `summary` is a recommended
convention, not a special command. The runner combines this output and the named
assertions with earlier observations, then completes the test when all requested
steps and checks are covered. Describe only what this script actually did and
verified; a reusable task should not tell the agent to ignore remaining test steps.

`platforms`, `deviceKinds`, and `frameworks` are optional task metadata, not
task inputs. A workspace test can reference this task with `taskId`; its CLI
runner validates the selected target and connected framework before execution.
Supported .NET framework IDs are `dotnet-maui`, `dotnet-ios`, and
`dotnet-android`.

`ansight-task.js` exports named permission constants:

```ts
import { Permission, IosPermission, AndroidPermission } from "./ansight-task.js";
await ansight.permissions.grant({ permission: Permission.Camera });
```

Each permission has hover documentation for platform availability, native mappings,
and official platform links. Read the full [Permission API Reference](https://www.ansight.ai/docs/workspace/permissions).
Nested task modules adjust the relative import path to the generated support file.

`ansight-task.d.ts` describes the available APIs and `tsconfig.json` enables
strict editor checks. They are generated support files; put your task logic in
your own `.ts` modules.

Inspect or run tasks with:

```sh
ansight task list --app-id com.example.app --repository .
ansight task run home.verify --app-id com.example.app --repository .
npx tsc -p ansight/tasks/tsconfig.json
```

Task runs require an already connected app session. Add `--headless` to
`task run` or `repo task run` when any permitted tool-requested device starts
should not open simulator/emulator windows. This does not create the initial
session, close existing windows, or restart running emulators.
